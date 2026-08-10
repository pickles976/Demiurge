using Demiurge;
using Riptide.Utils;
using Stride.Core.Diagnostics;

var log = GlobalLogger.GetLogger("Program");
RiptideLogger.Initialize(
    msg => log.Debug(msg),
    msg => log.Info(msg),
    msg => log.Warning(msg),
    msg => log.Error(msg),
    includeTimestamps: false);

using var application = new ClientApplication();
var arguments = Environment.GetCommandLineArgs()[1..];
#if SINGLEPLAYER_BUILD
// Release builds produced by the singleplayer workflow should be directly launchable. Explicit
// modes still work: ClientApplication checks --editor before --singleplayer.
if (!arguments.Contains("--singleplayer", StringComparer.OrdinalIgnoreCase))
    arguments = [.. arguments, "--singleplayer"];
#endif
application.Run(arguments);
