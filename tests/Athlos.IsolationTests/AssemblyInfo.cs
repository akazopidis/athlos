using Athlos.Testing;

// One PostgreSQL container for the whole test assembly.
[assembly: AssemblyFixture(typeof(AthlosDatabases))]
