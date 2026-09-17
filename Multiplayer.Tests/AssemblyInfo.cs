using Xunit;

// Every test class opens real loopback sockets and pumps them with short sleeps. Running classes in
// parallel on a two-core CI runner starves those pumps and turns timing-sensitive tests flaky.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
