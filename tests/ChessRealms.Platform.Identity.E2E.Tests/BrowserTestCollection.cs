using Xunit;

namespace ChessRealms.Platform.Identity.E2E.Tests;

[CollectionDefinition(nameof(BrowserTestCollection))]
public sealed class BrowserTestCollection : ICollectionFixture<BrowserAppFixture>;
