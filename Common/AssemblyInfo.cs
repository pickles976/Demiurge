using System.Runtime.CompilerServices;

// The transport conformance suite needs Message.Payload and Message.CreateForRead, which are internal
// because game code has no business touching raw payload bytes — only transports do. Following the
// pattern already established by Server/AssemblyInfo.cs.
[assembly: InternalsVisibleTo("DemiurgeCommon.Tests")]
