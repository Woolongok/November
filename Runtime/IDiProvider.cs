namespace November.DI
{
    /// <summary>
    /// Resolution plus injection: the "consumer" side of DI. Segregated from
    /// registration (<see cref="IServiceRegistry"/>) so consumers do not depend on
    /// registration APIs.
    ///
    /// Named <c>IDiProvider</c> rather than <c>IServiceProvider</c> so that a file with
    /// <c>using System; using November.DI;</c> does not hit CS0104 (ambiguous with
    /// <see cref="System.IServiceProvider"/>).
    /// </summary>
    public interface IDiProvider : IServiceResolver, IInjector
    {
    }
}
