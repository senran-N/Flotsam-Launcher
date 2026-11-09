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
                return true;
            case "shipTransfer":
                return trading;
            case "shipSteal":
            case "buildingMove":
            case "buildingStore":
            case "markerRemove":
            case "markerLimit":
            case "markerSize":
            case "markerFilter":
                return stealing;
            case "shipAttack":
                return friendlyFire;
            case "activate":
            case "deactivate":
            case "salvage":
            case "upgrade":
            case "cancelupgrade":
            case "assignmentLimit":
            case "rename":
            case "recipeSelect":
            case "recipeIncrease":
            case "recipeDecrease":
            case "recipeAmount":
            case "recipeContinuous":
            case "recipeDisableContinuous":
            case "queueCancel":
            case "producerPriority":
                return sabotage;
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
    [DataMember] public string displayMode = "game";
    [DataMember] public bool windowed;
    [DataMember] public Rules rules = new Rules();

    [OnDeserializing]
    private void Defaults(StreamingContext context)
    {
        address = "127.0.0.1";
        port = 27027;
        secret = "";
        mode = "shared";
        nickname = "";
        displayMode = "game";
        windowed = false;
        rules = new Rules();
    }
}
