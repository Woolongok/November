namespace November.DI
{

    /// <summary>
    /// Combines service resolution and injection — the "consumer" side of DI.
    /// Segregated from registration (IServiceRegistry) so resolution-only consumers
    /// don't depend on registration APIs.
    /// Follows the Interface Segregation Principle.
    /// </summary>
    public interface IServiceProvider : IServiceResolver, IInjector
    {
    }
}
