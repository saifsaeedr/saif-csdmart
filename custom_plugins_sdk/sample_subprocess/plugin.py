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
    main()
