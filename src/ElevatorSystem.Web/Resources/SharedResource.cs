namespace ElevatorSystem.Web.Resources;

/// <summary>
/// Marker type for the UI's strings: <c>SharedResource.resx</c> (en-US, the default) and
/// <c>SharedResource.pt-BR.resx</c>. Inject <c>IStringLocalizer&lt;SharedResource&gt;</c> and look strings up by <see cref="Text"/> keys.
/// </summary>
public sealed class SharedResource
{
    private SharedResource()
    {
    }
}
