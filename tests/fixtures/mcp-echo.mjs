#!/usr/bin/env node
// A tiny stdio MCP server for the add-in's tests (McpTests). Newline-delimited JSON-RPC 2.0, no dependencies, no network.
//   tools: echo (returns its text), and after the first call also echo2 (announced with notifications/tools/list_changed)
//          whoami  { ppid, cwd, clientName, initialized, env: selected variables, answers: replies to our own requests }
//          image   one PNG image block;  fail  isError: true;  slow  waits until cancelled (records the cancel)
//          cancels the requestIds cancelled so far;  crash  exits with code 3
// At initialize it asks the client two questions (ping, roots/list) and remembers the answers.
import readline from 'node:readline';

const PNG = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==';
const state = { clientName: null, initialized: false, answers: {}, cancels: [], calls: 0 };
const write = msg => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...msg }) + '\n');
const schema = { $schema: 'http://json-schema.org/draft-07/schema#', type: 'object', properties: { text: { type: 'string' } } };
const tools = () => [
  { name: 'echo', description: 'Echoes its text.', inputSchema: schema, annotations: { readOnlyHint: true } },
  { name: 'whoami', description: 'How the server was started.', inputSchema: { type: 'object' } },
  { name: 'image', description: 'One image.', inputSchema: { anyOf: [{ type: 'object' }] } },
  { name: 'fail', description: 'Always fails.', inputSchema: { type: 'object' } },
  { name: 'slow', description: 'Waits until cancelled.', inputSchema: { type: 'object' } },
  { name: 'cancels', description: 'Cancelled request ids.', inputSchema: { type: 'object' } },
  { name: 'crash', description: 'Exits.', inputSchema: { type: 'object' } },
  ...(state.calls > 0 ? [{ name: 'echo2', description: 'Appears after the first call.', inputSchema: schema }] : []),
];
const text = t => ({ content: [{ type: 'text', text: t }] });

function call(id, name, args) {
  state.calls++;
  if (state.calls === 1) setTimeout(() => write({ method: 'notifications/tools/list_changed' }), 50);
  switch (name) {
    case 'echo':
    case 'echo2':
      return write({ id, result: text(String(args?.text ?? '')) });
    case 'whoami': {
      const env = {};
      for (const k of ['CLAUDE_CODE_SESSION_ID', 'CLAUDE_PLUGIN_ROOT', 'CLAUDE_PROJECT_DIR', 'SOLIDWORKS_AGENT', 'SOLIDWORKS_AGENT_PID', 'AGENT_BRIDGE_NAME', 'EXTRA'])
        env[k] = process.env[k] ?? null;
      return write({ id, result: text(JSON.stringify({ ppid: process.ppid, cwd: process.cwd(), clientName: state.clientName, initialized: state.initialized, env, answers: state.answers })) });
    }
    case 'image':
      return write({ id, result: { content: [{ type: 'text', text: 'a dot' }, { type: 'image', data: PNG, mimeType: 'image/png' }] } });
    case 'fail':
      return write({ id, result: { content: [{ type: 'text', text: 'it went wrong' }], isError: true } });
    case 'slow':
      return; // never answers; the client cancels
    case 'cancels':
      return write({ id, result: text(JSON.stringify(state.cancels)) });
    case 'crash':
      process.exit(3);
    default:
      return write({ id, error: { code: -32602, message: `Unknown tool: ${name}` } });
  }
}

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', line => {
  if (!line.trim()) return;
  const msg = JSON.parse(line);
  if (msg.method === undefined) { // a reply to one of our questions
    state.answers[msg.id] = msg.error ? { error: msg.error.code } : { result: msg.result };
    return;
  }
  if (msg.id === undefined) {
    if (msg.method === 'notifications/initialized') state.initialized = true;
    if (msg.method === 'notifications/cancelled') state.cancels.push(msg.params?.requestId);
    return;
  }
  switch (msg.method) {
    case 'initialize':
      state.clientName = msg.params?.clientInfo?.name ?? null;
      write({ id: msg.id, result: { protocolVersion: msg.params?.protocolVersion, capabilities: { tools: { listChanged: true } }, serverInfo: { name: 'echo', version: '1.0.0' }, instructions: 'Echo server: use echo to repeat text.' } });
      write({ id: 'q-ping', method: 'ping' });
      write({ id: 'q-roots', method: 'roots/list' });
      return;
    case 'tools/list':
      return write({ id: msg.id, result: { tools: tools() } });
    case 'tools/call':
      return call(msg.id, msg.params?.name, msg.params?.arguments);
    default:
      return write({ id: msg.id, error: { code: -32601, message: `Method not found: ${msg.method}` } });
  }
});
rl.on('close', () => process.exit(0));
