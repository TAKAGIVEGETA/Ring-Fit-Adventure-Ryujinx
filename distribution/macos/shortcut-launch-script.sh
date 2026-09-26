#!/bin/sh
launch_arch="$(uname -m)"
if [ "$(sysctl -in sysctl.proc_translated)" = "1" ]
then
    launch_arch="arm64"
fi

# Ryujinx.app sets this with LSEnvironment, which doesn't apply when running its executable directly.
# It can't be set with LSEnvironment in the shortcut either, macOS refuses to launch script executables that have one.
export DOTNET_DefaultStackSize=200000

arch -$launch_arch {0} {1}
