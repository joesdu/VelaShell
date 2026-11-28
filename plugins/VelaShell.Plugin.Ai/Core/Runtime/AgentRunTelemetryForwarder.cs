using System.Runtime.CompilerServices;

// AgentRunTelemetry moved to VelaShell.AgentCore while retaining its legacy full name.
// The forwarder keeps binary consumers on the plugin assembly compatible.
[assembly: TypeForwardedTo(typeof(VelaShell.Plugin.Ai.Chat.Runtime.AgentRunTelemetry))]
