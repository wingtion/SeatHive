using System.ComponentModel.DataAnnotations;

namespace SeatHive.Api.Models
{
    // Paging for the list endpoints. Values outside the limits are rejected (400), not silently clamped.
    public class PageQuery
    {
        public const int MaxPage = 1_000_000;
        public const int MaxPageSize = 100;

        [Range(1, MaxPage)]
        public int Page { get; set; } = 1;

        [Range(1, MaxPageSize)]
        public int PageSize { get; set; } = 20;
    }

    // Seats get larger pages, so a seat map is one request.
    public class SeatPageQuery
    {
        public const int MaxPageSize = 500;

        [Range(1, PageQuery.MaxPage)]
        public int Page { get; set; } = 1;

        [Range(1, MaxPageSize)]
        public int PageSize { get; set; } = 200;
    }

    public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
}
