using System;

namespace November.DI
{

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ResolveAttribute : Attribute { }
}
