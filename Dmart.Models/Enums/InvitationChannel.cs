using System.Runtime.Serialization;

namespace Dmart.Models.Enums;

// Matches dmart Python's invitation `channel` claim value — "SMS" or "EMAIL"
// (uppercase, wire-verbatim). The string values here are written into both the
// JWT payload and the invitations.invitation_value DB column.
public enum InvitationChannel
{
    [EnumMember(Value = "SMS")]   Sms,
    [EnumMember(Value = "EMAIL")] Email,
}
