using System.Runtime.Serialization;

namespace Flotsam.Multiplayer;

[DataContract]
public sealed class Rules
{
    [DataMember] public bool stealing = true;
    [DataMember] public bool sabotage = true;
    [DataMember] public bool trading = true;
    [DataMember] public bool friendlyFire;

    [OnDeserializing]
    private void Defaults(StreamingContext context)
    {
        _ = context.Equals(default);
        stealing = true;
        sabotage = true;
        trading = true;
        friendlyFire = false;
    }

    /// <summary>
    /// Whether the room policy allows this command to act on another player's ship.
    /// The host is authoritative; the guest only renders the replicated result.
    /// </summary>
    public bool Allows(string kind)
    {
        switch (kind)
        {
            case "place":
            case "markerPlace":
            case "researchStart":
            case "researchCancel":
            case "buildingRepair":
            case "shipFlare":
            case "shipAllyRequest":
            case "shipAllyAccept":
            case "shipAllyReject":
            case "shipAllyBreak":
            case "nextBiomeReady":
                return true;
            case "shipTransfer":
            case "shipFloatBarrel":
            case "shipBarterOffer":
            case "shipBarterAccept":
            case "shipBarterCancel":
                return trading;
            case "shipSteal":
                return stealing;
            case "buildingMove":
            case "buildingStore":
            case "roadStore":
            case "moveBoat":
            case "markerRemove":
            case "markerLimit":
            case "markerSize":
            case "markerFilter":
                return false;
            case "shipAttack":
                return friendlyFire;
            case "activate":
            case "deactivate":
            case "salvage":
            case "upgrade":
            case "cancelupgrade":
            case "assignmentLimit":
            case "rename":
            case "decoPlace":
            case "decoRemove":
            case "decoCancelRemove":
            case "decoRename":
            case "energyConnect":
            case "energyDisconnect":
            case "storageTag":
            case "storageItem":
            case "storageBatch":
            case "storagePaste":
            case "aquaActive":
            case "aquaAddBrood":
            case "aquaRemoveBrood":
            case "distThreshold":
            case "distAmount":
            case "birdRename":
            case "birdFree":
            case "birdHouseFood":
            case "birdHouseExport":
            case "birdHouseGroup":
            case "recipeSelect":
            case "recipeIncrease":
            case "recipeDecrease":
            case "recipeAmount":
            case "recipeContinuous":
            case "recipeDisableContinuous":
            case "queueCancel":
            case "producerPriority":
                return false;
            default:
                return false;
        }
    }
}

[DataContract]
public sealed class NetConfig
{
    [DataMember(IsRequired = true)] public string role = "host";
    [DataMember] public string address = "127.0.0.1";
    [DataMember] public int port = 27027;
    [DataMember] public string secret = "";
    [DataMember(IsRequired = true)] public string profile = "";
    [DataMember] public bool autoStart;
    [DataMember] public string mode = "shared";
    [DataMember] public string nickname = "";
    [DataMember] public string ticket = "", admissions = "";
    [DataMember] public string displayMode = "game";
    [DataMember] public bool windowed;
    [DataMember] public Rules rules = new Rules();

    [OnDeserializing]
    private void Defaults(StreamingContext context)
    {
        _ = context.Equals(default);
        address = "127.0.0.1";
        port = 27027;
        secret = "";
        mode = "shared";
        nickname = "";
        ticket = admissions = "";
        displayMode = "game";
        windowed = false;
        rules = new Rules();
    }
}
