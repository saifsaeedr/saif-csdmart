# fish completion for dmart
# Install: cp dmart.fish ~/.config/fish/completions/
#      or: cp dmart.fish /usr/share/fish/vendor_completions.d/

set -l subcommands serve version settings set_password check health-check export import init cli help
set -l cli_modes c cmd s script
set -l cli_commands ls cd pwd switch mkdir create rm move cat print attach upload request progress import export help exit

# Top-level subcommands
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a "$subcommands"

# cli subcommand — modes
complete -c dmart -f -n "__fish_seen_subcommand_from cli; and not __fish_seen_subcommand_from $cli_modes $cli_commands" -a "$cli_modes"

# cli subcommand — direct commands (REPL commands usable in command mode)
complete -c dmart -f -n "__fish_seen_subcommand_from cli; and not __fish_seen_subcommand_from $cli_modes" -a "$cli_commands"

# cli c/cmd — after space name, complete CLI commands
complete -c dmart -f -n "__fish_seen_subcommand_from cli; and __fish_seen_subcommand_from c cmd" -a "$cli_commands"

# cli s/script — complete files
complete -c dmart -F -n "__fish_seen_subcommand_from cli; and __fish_seen_subcommand_from s script"

# export — complete files
complete -c dmart -rF -n "__fish_seen_subcommand_from export"

# import — complete zip files
complete -c dmart -rF -n "__fish_seen_subcommand_from import"

# check/health-check — no auto-complete (would need DB)
complete -c dmart -f -n "__fish_seen_subcommand_from check health-check"

# Descriptions
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a serve -d "Start the HTTP server"
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a version -d "Print version and build info"
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a settings -d "Print effective settings"
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a set_password -d "Set user password"
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a check -d "Run health checks"
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a export -d "Export space to zip"
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a import -d "Import from zip"
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a init -d "Initialize ~/.dmart"
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a cli -d "Interactive CLI client"
complete -c dmart -f -n "not __fish_seen_subcommand_from $subcommands" -a help -d "Print help"
