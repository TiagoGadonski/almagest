namespace Almagest.Application.Ports;

public enum QueryRoute
{
    Rag,
    Sql,
}

public interface IQueryRouter
{
    Task<QueryRoute> RouteAsync(string question, CancellationToken cancellationToken = default);
}
