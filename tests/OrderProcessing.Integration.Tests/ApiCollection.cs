using OrderProcessing.Integration.Tests.Infrastructure;

namespace OrderProcessing.Integration.Tests;

/// <summary>
/// Shares one <see cref="ApiFactory"/> across the integration suite.
/// </summary>
/// <remarks>
/// Booting a host per test class would be slow, and the tests are written to be
/// independent of each other's data — every test creates the orders it asserts on and
/// scopes queries to its own customer.
/// </remarks>
[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
