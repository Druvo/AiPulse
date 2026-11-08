// The top-level-statement Program class is internal by default; the end-to-end tests host the real app through
// WebApplicationFactory<Program>, which needs to see it. Several files may declare this; partial declarations merge.
public partial class Program { }
