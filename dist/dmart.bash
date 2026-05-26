# bash completion for dmart
# Install: cp dmart.bash /etc/bash_completion.d/dmart

_dmart() {
    local cur prev words cword
    _init_completion || return

    # Keep this list in sync with the case "..." labels in Program.cs's
    # subcommand dispatch. Both spellings (hyphen + underscore) are
    # included where Program.cs accepts both, so TAB suggests whichever
    # form the operator started typing.
    local subcommands="serve version settings passwd selfcheck preflight check health-check export import init cli migrate seed fix-query-policies fix_query_policies update-query-policies update_query_policies create-users-folders create_users_folders help"
    local cli_modes="c cmd s script"
    local cli_commands="ls cd pwd switch mkdir create rm move cat print attach upload request progress import export help exit"
    # Common per-subcommand flag sets reused below.
    local selfcheck_flags="--url --admin --password --password-stdin --jwt-bootstrap --space --subpath --keep -v --verbose -h --help"
    local serve_flags="--cxb-config --catalog-config -h --help"
    local preflight_flags="--dry-run --workers --output-dir --sample -v --verbose -h --help"

    case $cword in
        1)
            COMPREPLY=($(compgen -W "$subcommands" -- "$cur"))
            return
            ;;
        2)
            case "${words[1]}" in
                cli)
                    COMPREPLY=($(compgen -W "$cli_modes $cli_commands" -- "$cur"))
                    return
                    ;;
                check|health-check|export)
                    # space name — can't auto-complete without DB access
                    return
                    ;;
                migrate)
                    COMPREPLY=($(compgen -W "-q --quiet" -- "$cur"))
                    return
                    ;;
                fix_query_policies|fix-query-policies)
                    # optional <space> positional (free-form) or --dry-run
                    COMPREPLY=($(compgen -W "--dry-run" -- "$cur"))
                    return
                    ;;
                selfcheck)
                    COMPREPLY=($(compgen -W "$selfcheck_flags" -- "$cur"))
                    return
                    ;;
                serve)
                    COMPREPLY=($(compgen -W "$serve_flags" -- "$cur"))
                    return
                    ;;
                passwd)
                    # First arg is the shortname (positional). Free-form,
                    # no DB-backed suggestion; offer the help flag.
                    COMPREPLY=($(compgen -W "-h --help" -- "$cur"))
                    return
                    ;;
                preflight)
                    # First positional is the spaces-folder path; offer
                    # filesystem completion alongside the flag set.
                    if [[ "$cur" == -* ]]; then
                        COMPREPLY=($(compgen -W "$preflight_flags" -- "$cur"))
                    else
                        _filedir -d
                    fi
                    return
                    ;;
            esac
            ;;
        3)
            case "${words[1]}" in
                cli)
                    case "${words[2]}" in
                        c|cmd)
                            # space name
                            return
                            ;;
                        s|script)
                            # script file
                            _filedir
                            return
                            ;;
                        *)
                            COMPREPLY=($(compgen -W "$cli_commands" -- "$cur"))
                            return
                            ;;
                    esac
                    ;;
            esac
            ;;
        *)
            case "${words[1]}" in
                cli)
                    # After space name in command mode, complete CLI commands
                    if [[ "${words[2]}" == "c" || "${words[2]}" == "cmd" ]]; then
                        COMPREPLY=($(compgen -W "$cli_commands" -- "$cur"))
                        return
                    fi
                    ;;
                selfcheck)
                    # If the user just typed a flag that takes an argument,
                    # try _filedir or stay quiet; otherwise keep offering
                    # the flag set so multi-flag invocations TAB-complete.
                    case "$prev" in
                        --space|--subpath|--admin|--password|--url) return ;;
                    esac
                    COMPREPLY=($(compgen -W "$selfcheck_flags" -- "$cur"))
                    return
                    ;;
                serve)
                    case "$prev" in
                        --cxb-config|--catalog-config) _filedir; return ;;
                    esac
                    COMPREPLY=($(compgen -W "$serve_flags" -- "$cur"))
                    return
                    ;;
                preflight)
                    case "$prev" in
                        --output-dir) _filedir -d; return ;;
                        --workers|--sample) return ;;
                    esac
                    if [[ "$cur" == -* ]]; then
                        COMPREPLY=($(compgen -W "$preflight_flags" -- "$cur"))
                    else
                        _filedir -d
                    fi
                    return
                    ;;
                export)
                    _filedir
                    return
                    ;;
                import)
                    case "$prev" in
                        --checkpoint-file=*) return ;;
                    esac
                    # Operator can pass a zip file OR a directory — let
                    # _filedir suggest both. Subcommand flags also offered
                    # when the cur token starts with a dash.
                    if [[ "$cur" == -* ]]; then
                        COMPREPLY=($(compgen -W "-r --replace --fast --fast-parallelism= --batch-size= --type=zip --type=fs --space= --subpath= --resume --checkpoint-file= -h --help" -- "$cur"))
                    else
                        _filedir
                    fi
                    return
                    ;;
            esac
            ;;
    esac
}

complete -F _dmart dmart
