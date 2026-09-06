"""Entry point for the packaged .exe.

Double-clicking gets the window, since that is the only way anyone starts it
that way; the command-line flags still work for `--stop` and for running it
headless.
"""
import sys

from aram_overlay.__main__ import main

if __name__ == "__main__":
    argv = list(sys.argv[1:])
    if not any(a in ("--gui", "--stop", "--console") for a in argv):
        argv.append("--gui")
    sys.exit(main([a for a in argv if a != "--console"]))
