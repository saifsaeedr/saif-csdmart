#!/usr/bin/env python3
"""
Sample subprocess hook plugin for dmart.

Reads JSON lines from stdin, writes JSON lines to stdout.
Can be written in any language — Python used here for simplicity.

Deploy:
  mkdir -p ~/.dmart/plugins/sample_subprocess
  cp plugin.py ~/.dmart/plugins/sample_subprocess/sample_subprocess
  chmod +x ~/.dmart/plugins/sample_subprocess/sample_subprocess
  cp config.json ~/.dmart/plugins/sample_subprocess/
"""

import json
import sys

def handle_info():
    return {"shortname": "sample_subprocess", "type": "hook"}

def handle_hook(event):
    action = event.get("action_type", "?")
    space = event.get("space_name", "?")
    shortname = event.get("shortname", "?")
    user = event.get("user_shortname", "?")
    print(f"[sample_subprocess] {action} {space}/{shortname} by {user}", file=sys.stderr)
    return {"status": "ok"}

def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
            msg_type = msg.get("type", "")

            if msg_type == "info":
                response = handle_info()
            elif msg_type == "hook":
                response = handle_hook(msg.get("event", {}))
            else:
                response = {"status": "error", "message": f"unknown type: {msg_type}"}

            print(json.dumps(response), flush=True)
        except Exception as e:
            print(json.dumps({"status": "error", "message": str(e)}), flush=True)

if __name__ == "__main__":
    # Ctrl+C in the controlling terminal sends SIGINT to every process in the
    # foreground process group — this plugin included. dmart also receives it
    # and begins a clean shutdown which closes our stdin; that's the signal
    # we actually want to obey. Ignoring SIGINT here keeps us running until
    # dmart's stdin close drops us out of the `for line in sys.stdin` loop,
    # so shutdown is orderly and there's no traceback to scare operators.
    import signal
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    try:
        main()
    except KeyboardInterrupt:
        # Belt-and-suspenders: if SIG_IGN somehow didn't stick (e.g. the
        # plugin is launched from a context that resets signal handlers),
        # still exit silently rather than dumping a Python traceback.
        pass
