# WpfLens input operations

This glossary defines WpfLens input operations and describes their effects on the user's desktop.

## Terminology

**Background operation**

An action that can run while another application is in the foreground. It leaves the target's activation state and the system pointer position unchanged and allows the user to keep typing or clicking in other applications. If the action cannot meet these conditions, it fails.

_Avoid the terms “background thread operation” and “silent foreground operation.”_

**Physical input**

An interaction delivered through the operating system's mouse, keyboard or touch input channels. It may require the target to be in the foreground and can affect the user's current desktop interaction.

_Avoid using “background input” as a synonym._

**Background session**

A period during which automation preserves virtual hover and keyboard focus on the same target across input commands and intervening queries. While the session is active, automation has exclusive use of the target window's input. Ending the session returns control of that window to the user.

_Avoid the terms “single background command” and “permanent window lock.”_
