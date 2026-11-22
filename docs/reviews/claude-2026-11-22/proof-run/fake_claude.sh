#!/bin/bash
# Stub agent CLI for the proof run: swallows the prompt, waits for the harness to say the
# simulated tool calls are done, then prints a Claude Code style JSON result.
cat > "/private/tmp/claude-501/-Users-alo--claude-spares-spare-vgmmmW/74a45711-01b3-472f-81f2-e769b5371788/scratchpad/last_prompt.txt"
for i in $(seq 1 900); do [ -f "/private/tmp/claude-501/-Users-alo--claude-spares-spare-vgmmmW/74a45711-01b3-472f-81f2-e769b5371788/scratchpad/agent_go" ] && break; sleep 0.1; done
rm -f "/private/tmp/claude-501/-Users-alo--claude-spares-spare-vgmmmW/74a45711-01b3-472f-81f2-e769b5371788/scratchpad/agent_go"
echo '{"type":"result","subtype":"success","is_error":false,"result":"stub agent finished","num_turns":4}'
