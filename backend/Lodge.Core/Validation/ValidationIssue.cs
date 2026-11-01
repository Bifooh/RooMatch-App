namespace Lodge.Core.Validation;

/// <summary>
/// Structured validation issue. Codes are stable for clients and future matching.
/// Severity "error" blocks mutations; "warning" / "info" are advisory only.
/// </summary>
public sealed class ValidationIssue
{
    public required string Code { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public uint? ContractId { get; init; }
    public string? TenantCode { get; init; }
    public uint? ApartmentId { get; init; }
    public string? ApartmentNumber { get; init; }
    public uint? RelatedContractId { get; init; }
    public string? RelatedTenantCode { get; init; }

    public bool IsBlocking => Severity == Severities.Error;

    public static class Severities
    {
        public const string Error = "error";
        public const string Warning = "warning";
        public const string Info = "info";
    }

    public static class Codes
    {
        public const string DraftNotFound = "DRAFT_NOT_FOUND";
        public const string DraftNotEditable = "DRAFT_NOT_EDITABLE";
        public const string ContractNotFound = "CONTRACT_NOT_FOUND";
        public const string ContractTermMismatch = "CONTRACT_TERM_MISMATCH";
        public const string ApartmentNotFound = "APARTMENT_NOT_FOUND";
        public const string ApartmentInactive = "APARTMENT_INACTIVE";
        public const string FloorPlanMismatch = "FLOOR_PLAN_MISMATCH";
        public const string CapacityExceeded = "CAPACITY_EXCEEDED";
        public const string AssignmentLocked = "ASSIGNMENT_LOCKED";
        public const string DestinationLocked = "DESTINATION_LOCKED";
        public const string RestrictionRequiredApartment = "RESTRICTION_REQUIRED_APARTMENT";
        public const string RestrictionExcludedApartment = "RESTRICTION_EXCLUDED_APARTMENT";
        public const string RestrictionRequiredFloor = "RESTRICTION_REQUIRED_FLOOR";
        public const string RestrictionExcludedFloor = "RESTRICTION_EXCLUDED_FLOOR";
        public const string RestrictionMustNotShare = "RESTRICTION_MUST_NOT_SHARE";
        public const string NotAssigned = "NOT_ASSIGNED";
        public const string AlreadyUnassigned = "ALREADY_UNASSIGNED";
        public const string SwapSameContract = "SWAP_SAME_CONTRACT";
        public const string SwapRequiresBothAssigned = "SWAP_REQUIRES_BOTH_ASSIGNED";
        public const string LockOwnerRequired = "LOCK_OWNER_REQUIRED";
        public const string UnconfirmedAllergyColocation = "UNCONFIRMED_ALLERGY_COLOCATION";
        public const string UnresolvedRoommateRequest = "UNRESOLVED_ROOMMATE_REQUEST";
    }
}
