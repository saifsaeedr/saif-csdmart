# bash completion for dmart
# Install: cp dmart.bash /etc/bash_completion.d/dmart

_dmart() {
    local cur prev words cword
    _init_completion || return

    local subcommands="serve version settings set_password check health-check export import init cli help"
    local cli_modes="c cmd s script"
    local cli_commands="ls cd pwd switch mkdir create rm move cat print attach upload request progress import export help exit"

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
                export)
                    _filedir
                    return
                    ;;
                import)
                    _filedir '@(zip)'
                    return
                    ;;
            esac
            ;;
    esac
}

complete -F _dmart dmart
