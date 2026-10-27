using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>Marks a method as a tool the agent (and the UI) can call.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class AgentToolAttribute : Attribute
    {
        public AgentToolAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }
        public string Description { get; }
    }

    /// <summary>Describes one input property of a tool.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class ToolParamAttribute : Attribute
    {
        public ToolParamAttribute(string description)
        {
            Description = description;
        }

        public string Description { get; }
        public bool Required { get; set; }
    }

    /// <summary>A tool result in Claude API content-block shape (text and image blocks).</summary>
    public sealed class ToolResult
    {
        public List<JObject> Content { get; } = new List<JObject>();
        public bool IsError { get; set; }

        public ToolResult Text(string text)
        {
            Content.Add(new JObject { ["type"] = "text", ["text"] = string.IsNullOrEmpty(text) ? "(empty)" : text });
            return this;
        }

        public ToolResult Image(string base64, string mediaType = "image/jpeg")
        {
            Content.Add(new JObject
            {
                ["type"] = "image",
                ["source"] = new JObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = base64 },
            });
            return this;
        }

        public static ToolResult Ok(string text) => new ToolResult().Text(text);
        public static ToolResult Error(string text) => new ToolResult { IsError = true }.Text(text);

        public JObject ToJson() => new JObject { ["content"] = new JArray(Content), ["isError"] = IsError };
    }

    /// <summary>
    /// Discovers [AgentTool] methods, generates JSON schemas from their input classes, and dispatches calls.
    /// Tool methods look like:  ToolResult Name(ToolContext ctx, TInput input)  or  Task&lt;ToolResult&gt; ...
    /// </summary>
    public sealed class ToolRegistry
    {
        private sealed class Entry
        {
            public string Name;
            public string Description;
            public object Target;
            public MethodInfo Method;
            public Type InputType;
            public JObject Schema;
        }

        private static readonly SnakeCaseNamingStrategy Snake = new SnakeCaseNamingStrategy();

        private static readonly JsonSerializer InputSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = Snake },
        });

        private readonly Dictionary<string, Entry> _tools = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public void RegisterAll(object target)
        {
            foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<AgentToolAttribute>();
                if (attr == null) continue;
                var ps = method.GetParameters();
                if (ps.Length != 2 || ps[0].ParameterType != typeof(ToolContext))
                {
                    throw new InvalidOperationException($"Tool {attr.Name}: signature must be (ToolContext, TInput)");
                }
                _tools[attr.Name] = new Entry
                {
                    Name = attr.Name,
                    Description = attr.Description,
                    Target = target,
                    Method = method,
                    InputType = ps[1].ParameterType,
                    Schema = SchemaOf(ps[1].ParameterType),
                };
            }
        }

        public IEnumerable<string> Names => _tools.Keys;

        /// <summary>Tool definitions in Claude API shape (name, description, input_schema).</summary>
        public JArray Definitions()
        {
            return new JArray(_tools.Values.OrderBy(t => t.Name).Select(t => new JObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.Schema,
            }));
        }

        private static string Truncate(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max) + "...";

        public async Task<ToolResult> InvokeAsync(string name, JObject input, ToolContext ctx)
        {
            // A SolidWorks message box that would hang a model call is answered, and the agent hears what it said.
            if (Infrastructure.SwBusyGate.ModelFree(name)) return await InvokeCoreAsync(name, input, ctx);
            var before = DocTracker.Snapshot(ctx?.Sw);
            using (var dialogs = Infrastructure.DialogWatcher.Watch(name))
            {
                var result = await InvokeCoreAsync(name, input, ctx);
                foreach (var note in dialogs.Notes) result.Text(note);
                // Which windows the agent opened or used; a reminder when its idle ones pile up on a long job.
                var after = DocTracker.Snapshot(ctx?.Sw);
                DocTracker.Record(before, after);
                var nudge = name == "close_documents" ? null : DocTracker.Nudge(after);
                if (nudge != null) result.Text(nudge);
                return result;
            }
        }

        private async Task<ToolResult> InvokeCoreAsync(string name, JObject input, ToolContext ctx)
        {
            if (!_tools.TryGetValue(name, out var entry)) return ToolResult.Error($"Unknown tool '{name}'.");

            object typedInput;
            try
            {
                typedInput = (input ?? new JObject()).ToObject(entry.InputType, InputSerializer);
                var missing = entry.InputType.GetProperties()
                    .Where(p => p.GetCustomAttribute<ToolParamAttribute>()?.Required == true)
                    .Where(p => p.GetValue(typedInput) == null || (p.GetValue(typedInput) is string s && s.Length == 0))
                    .Select(p => SnakeCase(p.Name))
                    .ToList();
                if (missing.Count > 0) return ToolResult.Error("Missing required input: " + string.Join(", ", missing));
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Invalid input for {name}: {ex.Message}");
            }

            try
            {
                var started = DateTime.UtcNow;
                ToolResult result;
                // If SolidWorks dies during a tool call, the next start says which call it was (CrashGuard). Code cells, tool
                // macros and tool tests record themselves with their code, which is more useful than this line.
                var selfGuarded = name == "execute_csharp" || name == "run_tool_macro" || name == "test_tool";
                using (selfGuarded ? null : Infrastructure.CrashGuard.Enter("tool", null, name, Truncate(input?.ToString(Newtonsoft.Json.Formatting.None), 4000)))
                try
                {
                    // NativeFault: a fault inside a SolidWorks call becomes an error result instead of closing SolidWorks.
                    var raw = Infrastructure.NativeFault.Run(name, () => entry.Method.Invoke(entry.Target, new[] { ctx, typedInput }));
                    result = raw is Task<ToolResult> task ? await task : (ToolResult)raw;
                }
                catch (ToolException expected)
                {
                    return ToolResult.Error(expected.Message);
                }
                catch (Infrastructure.NativeFaultException fault)
                {
                    return ToolResult.Error(fault.Message);
                }
                Log.Info($"Tool {name} finished in {(DateTime.UtcNow - started).TotalMilliseconds:N0} ms{(result.IsError ? " (error)" : "")}");
                return result;
            }
            catch (TargetInvocationException tie) when (tie.InnerException is ToolException expected)
            {
                return ToolResult.Error(expected.Message);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                Log.Error($"Tool {name} threw", tie.InnerException);
                return ToolResult.Error($"{tie.InnerException.GetType().Name}: {tie.InnerException.Message}");
            }
            catch (Exception ex)
            {
                Log.Error($"Tool {name} threw", ex);
                return ToolResult.Error($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>The input schema of a tool's input type (also used by the test that builds every [AgentTool]'s schema).</summary>
        internal static JObject SchemaOf(Type inputType) => BuildSchema(inputType, 0, new HashSet<Type> { inputType });

        /// <summary>Nested input objects deeper than this are just {"type":"object"}: an unknown type fails soft instead of recursing.</summary>
        private const int MaxDepth = 6;

        private static JObject BuildSchema(Type inputType, int depth, HashSet<Type> path)
        {
            var props = new JObject();
            var required = new JArray();
            foreach (var p in inputType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue; // indexers aren't inputs
                var meta = p.GetCustomAttribute<ToolParamAttribute>();
                var schema = SchemaFor(p.PropertyType, depth + 1, path);
                if (meta != null) schema["description"] = meta.Description;
                var name = SnakeCase(p.Name);
                props[name] = schema;
                if (meta?.Required == true) required.Add(name);
            }
            var result = new JObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) result["required"] = required;
            return result;
        }

        private static JObject SchemaFor(Type t, int depth, HashSet<Type> path)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t == typeof(string)) return new JObject { ["type"] = "string" };
            if (t == typeof(bool)) return new JObject { ["type"] = "boolean" };
            if (t == typeof(int) || t == typeof(long)) return new JObject { ["type"] = "integer" };
            if (t == typeof(double) || t == typeof(float)) return new JObject { ["type"] = "number" };
            if (t.IsEnum) return new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(t)) };
            // Free-form JSON (a JObject "design" or "data" input): JObject is a class whose CLR properties (Parent, First,
            // Root...) lead back to themselves - walking them recursed until the stack overflowed and took SolidWorks down
            // while the add-in loaded. Before the IsClass and IEnumerable branches (JArray is both).
            if (typeof(JToken).IsAssignableFrom(t))
            {
                if (t == typeof(JArray)) return new JObject { ["type"] = "array" };
                if (t == typeof(JObject)) return new JObject { ["type"] = "object" };
                return new JObject(); // any JSON value
            }
            if (depth > MaxDepth) return new JObject { ["type"] = "object" };
            if (t.IsArray) return new JObject { ["type"] = "array", ["items"] = SchemaFor(t.GetElementType(), depth + 1, path) };
            // A dictionary is a JSON object, not an array of its pairs (it is also an IEnumerable); its values' schema goes in
            // additionalProperties, so the agent knows show_when takes lists and fills takes strings
            var dict = t.GetInterfaces().Concat(new[] { t }).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            if (dict != null) return new JObject { ["type"] = "object", ["additionalProperties"] = SchemaFor(dict.GetGenericArguments()[1], depth + 1, path) };
            if (typeof(IDictionary).IsAssignableFrom(t)) return new JObject { ["type"] = "object" };
            if (typeof(IEnumerable).IsAssignableFrom(t) && t.IsGenericType)
            {
                return new JObject { ["type"] = "array", ["items"] = SchemaFor(t.GetGenericArguments()[0], depth + 1, path) };
            }
            if (t.IsClass && t != typeof(object))
            {
                // Nested input objects (e.g. ToolFile[]). A type that contains itself is an object past its first level.
                if (!path.Add(t)) return new JObject { ["type"] = "object" };
                try
                {
                    return BuildSchema(t, depth, path);
                }
                finally
                {
                    path.Remove(t);
                }
            }
            return new JObject { ["type"] = "object" };
        }

        /// <summary>CaptureViews -> capture_views (same strategy the input binder uses).</summary>
        internal static string SnakeCase(string name) => Snake.GetPropertyName(name, false);
    }
}
