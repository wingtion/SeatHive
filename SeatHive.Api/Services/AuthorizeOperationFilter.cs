using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SeatHive.Api.Services
{
    // Marks an operation in the Swagger document as needing the bearer token only when its endpoint
    // really asks for one, so the anonymous endpoints are not shown with a lock.
    public class AuthorizeOperationFilter : IOperationFilter
    {
        public const string SchemeName = "Bearer";

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;

            var requiresToken = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
            if (!requiresToken) return;

            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = []
            });
        }
    }
}
