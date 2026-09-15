#!/usr/bin/env bash
# Regenerate playertrace-coverage/dashboard.html from the server template.
# Run after editing HTML_TEMPLATE in dashboard_server.py.
set -euo pipefail
cd "$(dirname "$0")/../.."
python3 -c "
import ast
src = open('Scripts/playertrace-coverage/dashboard_server.py', encoding='utf-8').read()
node = next(n for n in ast.parse(src).body if isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'HTML_TEMPLATE' for t in n.targets))
html = ast.literal_eval(node.value)
banner = '<!-- GENERATED from Scripts/playertrace-coverage/dashboard_server.py HTML_TEMPLATE. Do not hand-edit; regenerate with Scripts/playertrace-coverage/regen_static_dashboard.sh -->'
html = html.replace('<!DOCTYPE html>', '<!DOCTYPE html>\n' + banner, 1)
open('playertrace-coverage/dashboard.html', 'w').write(html)
print('regenerated playertrace-coverage/dashboard.html')
"
