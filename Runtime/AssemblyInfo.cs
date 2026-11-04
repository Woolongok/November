using System.Runtime.CompilerServices;

// Test hooks (resolution diagnostics, internal metadata) are internal, not public API.
[assembly: InternalsVisibleTo("November.DI.Tests")]
[assembly: InternalsVisibleTo("November.DI.Tests.PlayMode")]
