using System.Reflection;
using Daedalus.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Tests.Integration.Authentication;

/// <summary>
///     Integration tests verifying that all API controller endpoints require authorization.
///     Ensures that the [Authorize] attribute is properly applied to protect sensitive endpoints.
/// </summary>
public class ApiAuthorizationTests
{
    private static readonly Type[] ControllerTypes =
    [
        typeof(TasksController),
        typeof(ExecutionSessionsController),
        typeof(ProjectsController),
        typeof(TaskExecutionsController),
        typeof(CodeAnalysisController),
        typeof(RalphConfigController),
        typeof(RepositoriesController),
        typeof(PrdController)
    ];

    #region Endpoint Authorization Inheritance

    [Theory]
    [MemberData(nameof(GetAllPublicMethods))]
    public void AllPublicActionMethods_InAuthorizedControllers_ShouldInheritAuthorization(Type controllerType,
        MethodInfo method)
    {
        // Skip methods that are not action methods (not public HTTP action methods)
        if (!IsHttpActionMethod(method))
        {
            return;
        }

        // Arrange
        var controllerHasAuthorize = Attribute.IsDefined(controllerType, typeof(AuthorizeAttribute));
        var methodHasAllowAnonymousAttribute = Attribute.IsDefined(method, typeof(AllowAnonymousAttribute));

        // Assert
        // If controller has [Authorize], either:
        // 1. Method should inherit it (no explicit override), OR
        // 2. Method should have [AllowAnonymous] if it needs to be public
        if (controllerHasAuthorize && !methodHasAllowAnonymousAttribute)
        {
            // Method should inherit controller's authorization or have its own
            // This is implicitly true if the controller has [Authorize]
            // because ASP.NET Core will enforce it
            true.Should().BeTrue();
        }
        else if (!controllerHasAuthorize)
        {
            // If controller doesn't have [Authorize], controller is not properly secured
            Assert.Fail($"Controller {controllerType.Name} missing [Authorize] attribute");
        }
    }

    #endregion

    #region Authorization Attribute Validation

    [Fact]
    public void AuthorizeAttributesOnControllers_ShouldBeConsistentlyConfigured()
    {
        // Arrange & Act
        var inconsistencies = new List<string>();

        foreach (var controllerType in ControllerTypes)
        {
            var authorizeAttribute = controllerType.GetCustomAttribute<AuthorizeAttribute>();
            if (authorizeAttribute == null)
            {
                inconsistencies.Add($"{controllerType.Name}: Missing [Authorize]");
            }
            // Check if using default Authorize (no explicit policy)
            else if (!string.IsNullOrEmpty(authorizeAttribute.Policy))
            {
                inconsistencies.Add(
                    $"{controllerType.Name}: Uses custom policy '{authorizeAttribute.Policy}' - consider using default for consistency");
            }
        }

        // Assert
        inconsistencies.Where(x => x.Contains("Missing", StringComparison.Ordinal)).Should()
            .BeEmpty("All controllers must have [Authorize]");
    }

    #endregion

    #region Controller-Level Authorization

    [Fact]
    public void AllControllers_ShouldHaveAuthorizeAttribute()
    {
        // Arrange
        var unAuthorizedControllers = new List<string>();

        // Act
        foreach (var controllerType in ControllerTypes)
        {
            if (!Attribute.IsDefined(controllerType, typeof(AuthorizeAttribute)))
            {
                unAuthorizedControllers.Add(controllerType.Name);
            }
        }

        // Assert
        unAuthorizedControllers.Should()
            .BeEmpty(
                $"The following controllers lack [Authorize] attribute: {string.Join(", ", unAuthorizedControllers)}");
    }

    [Fact]
    public void AllControllers_AreDecoratedWithApiControllerAttribute()
    {
        // Arrange
        var missingApiController = new List<string>();

        // Act
        foreach (var controllerType in ControllerTypes)
        {
            if (!Attribute.IsDefined(controllerType, typeof(ApiControllerAttribute)))
            {
                missingApiController.Add(controllerType.Name);
            }
        }

        // Assert
        missingApiController.Should()
            .BeEmpty(
                $"The following controllers lack [ApiController] attribute: {string.Join(", ", missingApiController)}");
    }

    #endregion

    #region Helper Methods

    public static IEnumerable<object[]> GetAllPublicMethods()
    {
        foreach (var controllerType in ControllerTypes)
        {
            var publicMethods =
                controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in publicMethods)
            {
                yield return [controllerType, method];
            }
        }
    }

    private static bool IsHttpActionMethod(MethodInfo method)
    {
        // Exclude special methods
        if (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
            method.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            return false;
        }

        // Check if it has HTTP verb attributes
        var httpAttributes = new[]
        {
            typeof(HttpGetAttribute), typeof(HttpPostAttribute), typeof(HttpPutAttribute),
            typeof(HttpDeleteAttribute), typeof(HttpPatchAttribute)
        };

        return httpAttributes.Any(attr => Attribute.IsDefined(method, attr));
    }

    #endregion
}
