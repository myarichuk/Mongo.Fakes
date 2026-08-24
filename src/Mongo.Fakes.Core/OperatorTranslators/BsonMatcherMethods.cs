using System.Reflection;

namespace Mongo.Fakes.Core.OperatorTranslators;

/// <summary>Cached <see cref="MethodInfo"/> handles for <see cref="BsonMatchers"/>, shared by all translators.</summary>
internal static class BsonMatcherMethods
{
    private static MethodInfo Get(string name) =>
        typeof(BsonMatchers).GetMethod(name) ?? throw new MissingMethodException(nameof(BsonMatchers), name);

    public static readonly MethodInfo Eq = Get(nameof(BsonMatchers.Eq));
    public static readonly MethodInfo Ne = Get(nameof(BsonMatchers.Ne));
    public static readonly MethodInfo Gt = Get(nameof(BsonMatchers.Gt));
    public static readonly MethodInfo Gte = Get(nameof(BsonMatchers.Gte));
    public static readonly MethodInfo Lt = Get(nameof(BsonMatchers.Lt));
    public static readonly MethodInfo Lte = Get(nameof(BsonMatchers.Lte));
    public static readonly MethodInfo In = Get(nameof(BsonMatchers.In));
    public static readonly MethodInfo Nin = Get(nameof(BsonMatchers.Nin));
    public static readonly MethodInfo Exists = Get(nameof(BsonMatchers.Exists));
    public static readonly MethodInfo CheckType = Get(nameof(BsonMatchers.CheckType));
    public static readonly MethodInfo RegexMatch = Get(nameof(BsonMatchers.RegexMatch));
    public static readonly MethodInfo All = Get(nameof(BsonMatchers.All));
    public static readonly MethodInfo ElemMatch = Get(nameof(BsonMatchers.ElemMatch));
}
