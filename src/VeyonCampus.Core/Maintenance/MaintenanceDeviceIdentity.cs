namespace VeyonCampus.Core.Maintenance;

/// <summary>The stable maintenance key. A route address is deliberately excluded.</summary>
public readonly record struct MaintenanceDeviceKey
{
    public string CampusId { get; }
    public string PinnedIdentityFingerprint { get; }

    public MaintenanceDeviceKey(string campusId, string pinnedIdentityFingerprint)
    {
        WebsitePolicySigningKeyStore.ValidateCampusId(campusId);
        CampusId = campusId;
        PinnedIdentityFingerprint = MaintenanceValidation.Fingerprint(pinnedIdentityFingerprint,
            nameof(pinnedIdentityFingerprint));
    }
}

/// <summary>Current routing data plus a stable, pinned device identity.</summary>
public sealed class MaintenanceDeviceIdentity
{
    public MaintenanceDeviceKey Key { get; }
    public string CampusId => Key.CampusId;
    public string PinnedIdentityFingerprint => Key.PinnedIdentityFingerprint;
    public string RouteAddress { get; }

    public MaintenanceDeviceIdentity(string campusId, string pinnedIdentityFingerprint, string routeAddress)
    {
        Key = new MaintenanceDeviceKey(campusId, pinnedIdentityFingerprint);
        RouteAddress = MaintenanceValidation.RequiredText(routeAddress, nameof(routeAddress), 253);
    }

    public MaintenanceDeviceIdentity WithRouteAddress(string routeAddress) =>
        new(CampusId, PinnedIdentityFingerprint, routeAddress);
}
