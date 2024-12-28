using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.SystemTextJson;
using GitHubModel;

public partial class GitHubClient
{
    public static GraphQLHttpClient CreateGraphQLClient(string githubToken)
    {
        GraphQLHttpClient client = new GraphQLHttpClient(
            "https://api.github.com/graphql",
            new SystemTextJsonSerializer()
        );

        client.HttpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                scheme: "bearer",
                parameter: githubToken);

        return client;
    }

    public static async IAsyncEnumerable<(Issue Issue, string Label)> DownloadIssues(string githubToken, string org, string repo, Predicate<string> labelPredicate, int pageLimit, int[] retries)
    {
        await foreach (var item in DownloadItems<Issue>(githubToken, org, repo, labelPredicate, pageLimit, retries, "issues"))
        {
            yield return (item.Item, item.Label);
        }
    }

    public static async IAsyncEnumerable<(PullRequest PullRequest, string Label)> DownloadPullRequests(string githubToken, string org, string repo, Predicate<string> labelPredicate, int pageLimit, int[] retries)
    {
        var items = DownloadItems<PullRequest>(githubToken, org, repo, labelPredicate, pageLimit, retries, "pullRequests");

        await foreach (var item in items)
        {
            yield return (item.Item, item.Label);
        }
    }

    private static async IAsyncEnumerable<(T Item, string Label)> DownloadItems<T>(string githubToken, string org, string repo, Predicate<string> labelPredicate, int pageLimit, int[] retries, string itemQueryName) where T : Issue
    {
        int pageNumber = 1;
        string? after = null;
        bool hasNextPage = true;
        int loadedCount = 0;
        int? totalCount = null;
        byte retry = 0;

        while (hasNextPage && pageNumber < pageLimit)
        {
            Console.WriteLine($"Downloading {itemQueryName} page {pageNumber}... (limit: {pageLimit}){(retry > 0 ? $" (retry: {retry} of {retries.Length})" : "")}");

            Page<T> page;

            try
            {
                page = await GetItemsPage<T>(githubToken, org, repo, after, itemQueryName);
            }
            catch (Exception ex) when (
                ex is HttpIOException ||
                ex is HttpRequestException ||
                ex is GraphQLHttpRequestException ||
                ex is TaskCanceledException
            )
            {
                Console.WriteLine($"Exception caught during query.\n  {ex.Message}");

                if (++retry >= retries.Length)
                {
                    Console.WriteLine($"Retry limit of {retries.Length} reached. Aborting.");
                    break;
                }
                else
                {
                    Console.WriteLine($"Waiting {(retries[retry])} seconds before retrying...");
                    await Task.Delay(retries[retry] * 1000);
                    continue;
                }
            }

            if (after == page.EndCursor)
            {
                Console.WriteLine($"Paging did not progress. Cursor: '{after}'. Aborting.");
                break;
            }

            pageNumber++;
            after = page.EndCursor;
            hasNextPage = page.HasNextPage;
            loadedCount += page.Nodes.Length;
            totalCount ??= page.TotalCount;
            retry = 0;

            foreach (T item in page.Nodes)
            {
                // If there are more labels, there might be other applicable
                // labels that were not loaded and the model is incomplete.
                if (item.Labels.HasNextPage)
                {
                    Console.WriteLine($"{itemQueryName} {org}/{repo}#{item.Number} - Excluded from output. Not all labels were loaded.");
                    continue;
                }

                // Only items with exactly one applicable label are used for the model.
                string[] labels = Array.FindAll(item.LabelNames, labelPredicate);
                if (labels.Length != 1)
                {
                    Console.WriteLine($"{itemQueryName} {org}/{repo}#{item.Number} - Excluded from output. {labels.Length} applicable labels found.");
                    continue;
                }

                // Exactly one applicable label was found on the item. Include it in the model.
                Console.WriteLine($"{itemQueryName} {org}/{repo}#{item.Number} - Included in output. Applicable label: '{labels[0]}'.");

                yield return (item, labels[0]);
            }

            Console.WriteLine($"Total {itemQueryName} downloaded: {loadedCount} of {totalCount}. Cursor: '{after}'. {(hasNextPage ? "Continuing to next page.." : "No more pages.")}");
        }
    }

    private static async Task<Page<T>> GetItemsPage<T>(string githubToken, string org, string repo, string? after, string itemQueryName) where T : Issue
    {
        using GraphQLHttpClient client = CreateGraphQLClient(githubToken);

        string files = typeof(T) == typeof(PullRequest) ? "files (first: 100) { nodes { path } }" : "";

        GraphQLRequest query = new GraphQLRequest
        {
            Query = $$"""
                query Issues ($owner: String!, $repo: String!, $after: String) {
                    repository(owner: $owner, name: $repo) {
                        items:{{itemQueryName}} (after: $after, first: 100, orderBy: {field: CREATED_AT, direction: DESC}) {
                            nodes {
                                number
                                title
                                bodyText
                                labels(first: 25) {
                                    nodes { name },
                                    pageInfo { hasNextPage }
                                }
                                {{files}}
                            }
                            pageInfo {
                                hasNextPage
                                endCursor
                            }
                            totalCount
                        }
                    }
                }
                """,
            Variables = new
            {
                Owner = org,
                Repo = repo,
                After = after
            }
        };

        return (await client.SendQueryAsync<RepositoryQuery<T>>(query)).Data.Repository.Items;
    }
}
