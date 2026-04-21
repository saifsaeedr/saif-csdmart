namespace Dmart.Models.Api;

public sealed record HttpValidationError(List<ValidationError>? Detail);

public sealed record ValidationError(
    List<object> Loc,
    string Msg,
    string Type);
