using ZeroAlloc.Validation;

namespace Daedalus.Api.Middleware;

/// <summary>Non-generic seam over the generated <see cref="ValidatorFor{T}"/>.</summary>
public interface IValidationAdapter
{
    Type TargetType { get; }
    ValidationResult Validate(object instance);
}

public sealed class ValidationAdapter<T>(ValidatorFor<T> validator) : IValidationAdapter
{
    public Type TargetType => typeof(T);

    public ValidationResult Validate(object instance) => validator.Validate((T)instance);
}
