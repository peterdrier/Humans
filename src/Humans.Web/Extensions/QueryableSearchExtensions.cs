using System.Linq.Expressions;

namespace Humans.Web.Extensions;

public static class QueryableSearchExtensions
{
    public static IQueryable<T> WhereAnyContainsInsensitive<T>(
        this IQueryable<T> source,
        string searchTerm,
        params Expression<Func<T, string?>>[] selectors)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchTerm);
        ArgumentNullException.ThrowIfNull(selectors);

        if (selectors.Length == 0)
        {
            return source;
        }

        var normalizedSearch = searchTerm.ToLowerInvariant();
        var parameter = Expression.Parameter(typeof(T), "item");
        Expression? combinedPredicate = null;

        foreach (var selector in selectors)
        {
            var body = new ParameterReplaceVisitor(selector.Parameters[0], parameter).Visit(selector.Body)!;
            var notNull = Expression.NotEqual(body, Expression.Constant(null, typeof(string)));
            var toLower = Expression.Call(body, nameof(string.ToLower), Type.EmptyTypes);
            var contains = Expression.Call(toLower, nameof(string.Contains), Type.EmptyTypes, Expression.Constant(normalizedSearch));
            var predicate = Expression.AndAlso(notNull, contains);

            combinedPredicate = combinedPredicate is null
                ? predicate
                : Expression.OrElse(combinedPredicate, predicate);
        }

        return source.Where(Expression.Lambda<Func<T, bool>>(combinedPredicate!, parameter));
    }

    private sealed class ParameterReplaceVisitor(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == from ? to : base.VisitParameter(node);
        }
    }
}
