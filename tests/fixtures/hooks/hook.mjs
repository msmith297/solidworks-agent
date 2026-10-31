#!/usr/bin/env node
// A fake Claude Code hook for the add-in's tests (HookRunnerTests). It reads the event JSON from stdin, then behaves as
// its first argument says:
//   context   JSON hookSpecificOutput.additionalContext       plain     plain text on stdout
//   stdin     the stdin it got, as additionalContext          env       selected environment variables, as additionalContext
//   block2    stderr + exit 2                                 jsonblock { decision: "block", reason }
//   deny|ask|allow  PreToolUse permissionDecision             stop      { continue: false, stopReason }
//   system    { systemMessage }                               exit1     stderr + exit 1
//   rewake    after 300 ms: stderr text + exit 2 (asyncRewake)
//   sleep     starts a child node, writes "<own pid> <child pid>" to the file in argument 2, waits 60 s
//   ppid      writes "<own pid> <ppid>" to the file in argument 2, waits 5 s, prints nothing
// No network, no files outside the paths the test passes.
import fs from 'node:fs';
import { spawn } from 'node:child_process';

const mode = process.argv[2];
const chunks = [];
for await (const c of process.stdin) chunks.push(c);
const raw = Buffer.concat(chunks).toString('utf8');
const input = JSON.parse(raw || '{}');
const out = obj => process.stdout.write(JSON.stringify(obj));
const ctx = text => out({ hookSpecificOutput: { hookEventName: input.hook_event_name, additionalContext: text } });

switch (mode) {
  case 'context': ctx(`context from ${input.hook_event_name}`); break;
  case 'plain': process.stdout.write('plain words from the hook\n'); break;
  case 'stdin': ctx(raw); break;
  case 'env': {
    const env = {};
    for (const k of ['CLAUDE_CODE_SESSION_ID', 'CLAUDE_PLUGIN_ROOT', 'CLAUDE_PLUGIN_DATA', 'CLAUDE_PROJECT_DIR', 'SOLIDWORKS_AGENT', 'SOLIDWORKS_AGENT_PID', 'AGENT_BRIDGE_NAME', 'ANTHROPIC_TEST_SECRET_B', 'OPENAI_TEST_SECRET_B'])
      env[k] = process.env[k] ?? null;
    ctx(JSON.stringify(env));
    break;
  }
  case 'block2': process.stderr.write('blocked: ünïcode reason'); process.exitCode = 2; break;
  case 'jsonblock': out({ decision: 'block', reason: 'json reason' }); break;
  case 'deny': case 'ask': case 'allow':
    out({ hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: mode, permissionDecisionReason: `${mode} reason` } });
    break;
  case 'stop': out({ continue: false, stopReason: 'stop now' }); break;
  case 'system': out({ systemMessage: 'a notice for the user' }); break;
  case 'exit1': process.stderr.write('something failed'); process.exitCode = 1; break;
  case 'rewake':
    await new Promise(r => setTimeout(r, 300));
    process.stderr.write(`New messages for ${input.session_id}`);
    process.exitCode = 2;
    break;
  case 'sleep': {
    const child = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 60000)'], { stdio: 'ignore' });
    fs.writeFileSync(process.argv[3], `${process.pid} ${child.pid}`);
    await new Promise(r => setTimeout(r, 60000));
    break;
  }
  case 'ppid':
    fs.writeFileSync(process.argv[3], `${process.pid} ${process.ppid}`);
    await new Promise(r => setTimeout(r, 5000));
    break;
}
