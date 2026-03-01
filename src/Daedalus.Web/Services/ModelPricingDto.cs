namespace Daedalus.Web.Services;

public record ModelPricingDto(
    string ModelId,
    string DisplayName,
    decimal InputPricePerMillion,
    decimal OutputPricePerMillion);
