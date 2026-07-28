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
application.Run(Environment.GetCommandLineArgs()[1..]);
