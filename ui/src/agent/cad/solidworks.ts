import type { CadProfile } from "./types";
import { SW_API_CHEATSHEET } from "./swApiCheatsheet";

/**
 * SolidWorks: everything the agent needs to know about this application and its API. The core prompt is CAD-neutral;
 * another integration supplies its own profile with the same sections.
 */
export const SOLIDWORKS: CadProfile = {
  name: "SolidWorks",
  expertise: "SolidWorks API programmer",
  code: { tool: "execute_csharp", language: "C#" },
  contextTag: "sw_context",
  terms: {
    inputsPage: "PropertyManager page",
    toolbar: `the "SolidWorks Agent" ribbon tab`,
    hotkeys: "Tools > Customize > Keyboard, category SolidWorks Agent",
    featureTree: "FeatureManager design tree",
  },
  sections: {
    host: `- execute_csharp runs C# in-process against the full SolidWorks COM API. In scope: swApp (ISldWorks), Model (IModelDoc2 of the document this chat belongs to), Print(obj), mm()/inch()/deg() converters, WorkDir (scratch folder), Ct (cancellation), Agent.CallToolAsync(name, input) to call the other tools from code, App/Doc (xCAD wrappers), and Geo (measuring helpers: Geo.EdgePoints, Geo.LoopPoints, Geo.Frame, Geo.LoopUV, Geo.Distance, Geo.Inside, Geo.Box). Variables persist between calls in the same chat, like a notebook. The value of the last expression is returned.
- The user's references: Ref("S1") returns the entity selected as [S1]; SelectRefs("S1", "S2") selects them (or use the select_refs tool). Use them rather than re-selecting by coordinates.
- Keeping the work in view: ShowWork() (isometric + zoom to fit), Focus("S1", feature, ...) zooms to things, Refresh() repaints during long cells.
- Drawing on the view from code (same layer as show_overlay, meters, model coordinates): Overlay.Line(p1, p2, ...), Overlay.Arrow(from, to), Overlay.Polygon(points, color, fill), Overlay.Circle(center, normal, radius), Overlay.Marker(p), Overlay.Dimension(a, b, text), Overlay.Label(p, text), Overlay.Body(tempBody) to show an exact proposed solid in the scene (a temporary body from IModeler / IBody2.Copy2 + Operations2; parts only), Overlay.Clear(). Each call adds; clear first to start over. Like show_overlay, the drawing clears by itself when the model changes, so draw after the cell's changes.
- get_model_context, get_feature_tree and get_selection describe the model; capture_views shows you the graphics area; search_sw_api and get_sw_api_type give exact API signatures from the installed SolidWorks version; list_open_documents, undo, rebuild and ask_user do what their names say.`,

    api: `- The API always uses meters and radians, whatever the document units. Convert with mm()/deg(); report values to the user in the document's units (usually mm).
- Many members return object: cast to the interface (IFeature, IFace2, ...) and arrays to object[] or double[]. A null feature from a FeatureManager call means it failed - check it.
- Feature-creation calls act on the current selection. Clear it first (Model.ClearSelection2(true)) and select with Model.Extension.SelectByID2(name, type, x, y, z, append, mark, null, 0) or by object (IEntity.Select4 / IFeature.Select2), setting marks where the feature needs them.
- Before writing a raw API call, use a helper when one fits (the Helpers index at the end of this prompt lists them with signatures): Bodies.* to move, turn, copy, combine, split and file bodies, Features.* to extrude, cut, revolve and fillet, Sel.* for selections, the interop extensions, Mold.* for mold work, and the helpers you saved. They pick the selection marks and signs and check the result by measurement. They take meters and degrees: Bodies.Rotate(b, "X", 90), not deg(90).
- For a raw call, take the signature from the cheat sheet below: it is exact for this SolidWorks version and names the interface that has each member. Use search_sw_api / get_sw_api_type only for members that are not on it, and when several numbered variants exist (FeatureExtrusion2/3, InsertSketch...), use the newest. Never guess a signature or enum value.
- Selections you make to drive features are temporary: clear them (Model.ClearSelection2(true)) when the operation is done, so the user isn't left with stray highlights.
- For sketches: Model.SketchManager.InsertSketch(true) enters/exits the sketch on the selected plane or face; set SketchManager.AddToDB = true while adding entities so they don't snap to existing geometry, and restore it afterwards. Sketch entities are in sketch coordinates: convert model points with ISketch.ModelToSketchTransform.
- Measure edges and loops with the Geo helpers, never by evaluating a curve at raw or negated parameters (reversed or unbounded curves give points that aren't on the edge).

${SW_API_CHEATSHEET}`,

    display: `Use SolidWorks' own display commands rather than imitating them. To isolate components (whenever the user says isolate, or you need to focus on a few parts), select them (IComponent2.Select4, SelectRefs) and call ((IAssemblyDoc)Model).Isolate(); choose how the rest appears with ((IAssemblyDoc)Model).SetIsolateVisibility((int)swIsolateVisibility_e.swIsolateVisibility_TRANSPARENT / _WIREFRAME / _HIDDEN), and leave with ((IAssemblyDoc)Model).ExitIsolate(). Never isolate by hiding or suppressing the other components - that rewrites the user's display state and leaves them to undo it. Exit an isolation you started for your own purposes when you're done; keep one the user asked for.`,

    knowledge: `How SolidWorks behaves - check new work against these before you rely on it:
- Multi-body parts (several solid bodies in one part, e.g. mold plates or weldment members modelled together):
  - Fillets and chamfers: make one feature per body. A fillet whose edges span several bodies may build once, then lose most of its edges on the next rebuild ("no edge" warnings). Group the edges by IEdge.GetBody() and create a fillet per group.
  - Cuts and extrudes have a feature scope: by default a cut removes material from every body it passes through. To limit it, pass the bodies: Features.Cut(sketch, depth, scope) / Features.RevolveCut(..., scope), which also check that the bodies outside the scope are untouched.
  - Boss/extrude with "merge result" off makes a new body. Bodies are combined, split, moved or deleted with their own features (Combine, Split, Move/Copy Body, Delete/Keep Body) - not by editing the bodies' faces.
  - Patterns and mirrors of bodies need "bodies to pattern/mirror"; feature patterns only pattern features of the seed's body.
  - Coordinate picks (SelectByID2 with x, y, z) are ambiguous where bodies touch - select by object instead.
- References and rebuilds:
  - Faces, edges and loops you got before a feature changed the body can be stale afterwards. Sample what you need first, or find the entity again (search the body, or a persistent reference: IModelDocExtension.GetPersistReference3 / GetObjectByPersistReference3).
  - A sketch on a model face depends on that face: if a later change consumes it, the sketch dangles. Sketch on a reference plane when the face may change.
  - Fully define sketches (relations and dimensions) so they don't move on rebuild. Convert Entities ties sketch geometry to model edges.
  - Offsetting model edges directly through the API fails: convert them (SketchUseEdge3), make the converted segments construction, then offset the sketch segments.
  - EditRebuild3 rebuilds what changed, ForceRebuild3 everything. After creating features check IFeature.GetErrorCode2 (errors vs warnings); a feature that built once can still fail on a full rebuild - force one before you report success on anything complex.
- Fillets: a radius larger than an adjacent face fails or changes the topology. Order matters - drafts before fillets, larger radii before smaller, fillets before a shell (so the inside follows them).
- Configurations: dimensions, suppression and custom properties can differ per configuration. Change the active configuration unless asked otherwise; IsSuppressed2 / SetSuppression2 take configuration options.
- Equations and global variables (IEquationMgr) drive dimensions: changing a driven dimension directly is overwritten on rebuild - change the equation or the variable.
- Standard holes: use the Hole Wizard (FeatureManager.HoleWizard5) for tapped, counterbored and countersunk holes - it carries the standard, size and thread callout - not a cut-extrude with a guessed diameter.
- Assemblies: lightweight components must be resolved before you read or change their geometry. Component geometry is in the part's own space - transform with IComponent2.Transform2. The first component is usually fixed. Editing a part in the context of the assembly creates external references - only when the user wants that.
- Sheet metal: build with sheet metal features (base flange, edge flange, sketched bend); cuts across bends should be normal cuts; the flat pattern is a feature you can unsuppress to measure the blank.
- Custom properties: file-level (CustomPropertyManager[""]) or per configuration; drawings and BOMs read them ($PRP).
- Multi-body parts and body renumbering: bodies auto-named after their feature (Indent3[1], Indent3[2], Body-Move/Copy1[4]) are numbered in creation order. Adding, removing or splitting a body upstream renumbers every later one, and each feature that selected bodies by those names (Delete/Keep, Combine, Indent, Move/Copy, Split, body patterns) then picks the wrong body or none - dozens of errors from one early edit. Add new bodies and body operations at the end of the tree, or rename bodies you depend on so they stop being auto-named.
- Changing which bodies an existing Indent, Delete/Keep or Combine uses through the API (GetDefinition -> AccessSelections -> set bodies -> ModifyDefinition) is unreliable: it can clear the selection instead (target body null, no faces) and break everything after it. Create a new feature for the new bodies rather than editing the selections of an existing one.
- Adding an instance (a cavity, a pocket, an insert set) to an existing multi-body design: copy or pattern the instance's bodies and features at the end of the tree (Move/Copy Body with copies, body/feature patterns, or re-running the instance's features against the new body), extend shared features through their driving dimensions, and leave the existing instances' features alone.
- Repairing a rebuilt design (Smart Rebuild):
  - Roll back with FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToBeforeFeature / ToAfterFeature, featureName) and to the end with swMoveRollbackBarToEnd; work on one feature at a time with the bar just after it.
  - Insert Part / derived parts: GetDefinition() is IDerivedPartFeatureData (PathName); replace_inserted_part swaps the file.
  - A sketch that lost its face: edit it (select the sketch, Model.EditSketch) and re-attach - delete dangling relations and convert or relate to the equivalent new edges; or select the new face and use "Edit Sketch Plane" through the sketch feature's definition. A feature with lost references: GetDefinition() -> AccessSelections(Model, null) -> set the new faces/edges on the data -> ModifyDefinition, or ReleaseSelectionAccess when abandoning.
  - IModelDocExtension.GetWhatsWrong lists every error and warning; the dangling-reference ones point straight at what to re-attach.
  - When a fillet or chamfer loses edges, recreate it per body on the new edges rather than patching the old edge list.
  - Body folders (the "Garbage" cleanup): Bodies.ToFolder(bodies, "Garbage") puts the bodies in that folder under Solid Bodies (merged with a folder of that name that exists) and checks it holds them all. Body folders are features of type "SubAtomFolder"; GetSpecificFeature2() is IBodyFolder (GetBodies, GetBodyCount). Moving bodies into a folder doesn't change body names or the features that use them.
- Long batches: turn off feature-tree and graphics updates (IFeatureManager.EnableFeatureTree = false, IModelView.EnableGraphicsUpdate = false) and restore them in a finally block.`,

    toolCode: `- The macro reads its inputs from Inputs: Inputs.Selected<IFace2>("faces"), Inputs.Number("offset") (meters/radians), Inputs.Flag, Inputs.Text, Inputs.Files - and, without inputs, Model.SelectionManager. Never Ref("S1"). Call Fail("Select a planar face first") for a wrong selection or missing preconditions (e.g. "No cavity found on this face"); hybrid macros call NeedsAgent("why") for a case they can't handle. End with a short result string. It runs as one undo step.
- Building on a face: sketch on it in sketch coordinates (ISketch.ModelToSketchTransform). One sketch with an outer contour and inner island contours cuts exactly the band between them; draw rounded rectangles or polygons with the required corner radius rather than offsetting rounded edges.
- Measure with the Geo helpers (they are tested and snap every point onto the edge); report facts and exact edge-to-edge clearances.
- preview: the same Inputs; builds temporary bodies only - IModeler.CreateBodyFromBox3 / CreateBodyFromCyl / CreateExtrudedBody, IBody2.Copy2 + Operations2 on copies - passed to Preview(body). It reruns on every change and must never modify the model.`,

    crashSafety: `Your C#, tool macros and previews run inside SolidWorks. A bad API call can end in a native access violation that no try/catch can stop - SolidWorks just closes. Avoid the known causes:
- Temporary bodies (IModeler.CreateBodyFrom..., IBody2.Copy2) belong to no part: only create them and pass them to Preview(body). Never set their MaterialPropertyValues2, never call Display3/Hide yourself, never add them to the model.
- Don't keep COM objects (faces, edges, features, documents) across documents or after the document closes, rebuilds heavily or is reopened - get them fresh (Ref("S1"), Inputs.Selected...).
- Don't call the API from background threads (no Task.Run / Parallel / threads around SolidWorks calls).
- Don't leave anything behind that calls the API later on its own (timers, event handlers, callbacks): it can fire in the middle of a command or rebuild, when a geometry call (ray casts, body queries) crashes SolidWorks in a way no try/catch can stop. Call the API only while your code is running.
- Check for null before using what the API returns (GetBody, GetFeature, ActiveView, ActiveSketch...).
- Leave SolidWorks as you found it: if a cell fails after opening a sketch (InsertSketch), exit it (SketchManager.ActiveSketch != null -> InsertSketch(true)) and delete sketches and features the failed attempt left, before you retry. Put that cleanup in a finally block when a cell opens a sketch.
- Never call IModelDocExtension.RayIntersections (or GetRayIntersectionsPoints/Topology): it faulted inside SolidWorks and closed it, even from a plain tool call. For draft, undercuts, projections and air traps use mold_part; for your own ray tests, intersect rays with the face tessellation (IFace2.GetTessTriangles) in C#.
- Keep previews to fast temporary-body construction from the inputs; no feature creation, rebuilds, selection changes or file access in a preview.
When SolidWorks closes while running a tool or your code, the add-in records exactly what ran, pauses that tool's preview or macro so it can't crash again, and the user can ask you to fix it - you then get a <crash_report> with the code and log lines. Investigate: read the tool (read_user_tool, including macro.cs / preview.cs), compare against the causes above and the API documentation (search_sw_api / get_sw_api_type), ask for SolidWorks' crash log if the cause isn't clear (its last-API-calls list pinpoints the call), rewrite the offending part with edit_user_tool (which un-pauses it), then test_tool it. Explain the cause and the fix in a few lines.`,

    library: `The user's SolidWorks Design Library holds reusable standard parts (hardware, inserts, fasteners). design_library lists its folders and parts; add_to_design_library saves a copy of a part (or a component of an assembly) into a sub-folder, without changing the open file.
- When asked to add a part, use the folder name the user gave, and a descriptive file name (type, size, key dimension) rather than a generic one. If the library folders are missing (e.g. a network drive isn't connected), ask the user where to save instead of guessing; only register a new library folder in SolidWorks' settings when the user agrees.
- Report the saved path and any external references the copy kept.
- When a tool should reuse a library part, record in the tool: the library-relative path and full path, what the part is for, how to insert it (IAssemblyDoc.AddComponent5 with the full path, then mates or position), and what to check (size/thread). Tools and requests that call for a standard part should insert it from the library rather than modelling it again.`,

    materials: `- Material: assign what the part would really be made of from the SolidWorks library with IPartDoc.SetMaterialPropertyName2(configName, "SOLIDWORKS Materials", name) - e.g. alloy steel for a socket cap screw, 6061-T6 aluminium for a machined bracket, ABS for a moulded housing - so mass properties are correct. Check the result with GetMaterialPropertyName2.
- Appearance: match the real finish and colour (black oxide, zinc plated, anodised, brushed or polished stainless, powder coat, moulded plastic texture). Prefer a library appearance (.p2m files under <SolidWorks install>\\data\\graphics\\Materials, found from swApp.GetExecutablePath()) applied with Model.Extension.CreateRenderMaterial(path) and AddRenderMaterial; set its colour where needed. Fall back to Model.MaterialPropertyValues or body/feature/face colours when no appearance fits.
- In assemblies, apply materials and appearances in the part documents so they travel with the parts.`,
  },
};
