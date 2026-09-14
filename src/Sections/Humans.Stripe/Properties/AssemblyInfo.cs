using System.Runtime.CompilerServices;

// Castle DynamicProxy — the InternalsVisibleTo convention every section carries so a mock
// framework can proxy its internal types. Humans.Stripe.Tests substitutes nothing today; the
// connector's seam is an interface its tests construct directly.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
