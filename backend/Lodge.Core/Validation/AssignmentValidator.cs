namespace Lodge.Core.Validation;

/// <summary>
/// Authoritative assignment validation. Pure over <see cref="DraftState"/> so
/// matching and the manual workspace can share the same checks.
/// </summary>
public sealed class AssignmentValidator
{
    public static readonly IReadOnlyList<string> ValidationLimitations =
    [
        "Private-room / bedroom-slot capacity is not modelled (proposal only; schema has no room_type).",
        "Ward mapping is not modelled (apartments.ward_id proposed, not applied).",
        "Accessibility needs / adapted apartments are not modelled.",
        "animal_allergy_restriction and has_animal are legacy preference flags without staff-confirmation fields; co-location is surfaced as a warning only, never as a hard block.",
        "Roommate-request name resolution is not performed; pending/unresolved wording is shown unchanged.",
    ];

    /// <summary>Validate placing <paramref name="contractId"/> into <paramref name="apartmentId"/> (or unassigning when null).</summary>
    public IReadOnlyList<ValidationIssue> ValidateMove(DraftState state, uint contractId, uint? apartmentId)
    {
        var issues = new List<ValidationIssue>();
        AddDraftEditable(state, issues);
        if (issues.Count > 0) return issues;

        if (!state.Contracts.TryGetValue(contractId, out var contract))
        {
            issues.Add(Issue(ValidationIssue.Codes.ContractNotFound, ValidationIssue.Severities.Error,
                $"Contract {contractId} was not found.", contractId: contractId));
            return issues;
        }

        if (contract.TermId != state.TermId)
        {
            issues.Add(Issue(ValidationIssue.Codes.ContractTermMismatch, ValidationIssue.Severities.Error,
                $"Contract {contractId} (tenant {contract.TenantCode}) belongs to term {contract.TermId}, not draft term {state.TermId}.",
                contractId: contractId, tenantCode: contract.TenantCode));
            return issues;
        }

        state.AssignmentsByContract.TryGetValue(contractId, out var existing);

        if (apartmentId is null)
        {
            if (existing is null)
            {
                issues.Add(Issue(ValidationIssue.Codes.AlreadyUnassigned, ValidationIssue.Severities.Error,
                    $"Contract {contractId} ({contract.TenantCode}) is already unassigned.",
                    contractId: contractId, tenantCode: contract.TenantCode));
                return issues;
            }

            if (existing.IsLocked)
            {
                issues.Add(LockedIssue(contract, existing));
            }

            return issues;
        }

        if (!state.Apartments.TryGetValue(apartmentId.Value, out var apt))
        {
            issues.Add(Issue(ValidationIssue.Codes.ApartmentNotFound, ValidationIssue.Severities.Error,
                $"Apartment {apartmentId.Value} was not found.",
                contractId: contractId, tenantCode: contract.TenantCode, apartmentId: apartmentId));
            return issues;
        }

        if (!apt.IsActive)
        {
            issues.Add(Issue(ValidationIssue.Codes.ApartmentInactive, ValidationIssue.Severities.Error,
                $"Apartment {apt.ApartmentNumber} is inactive and cannot receive assignments.",
                contractId: contractId, tenantCode: contract.TenantCode,
                apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber));
        }

        if (existing is not null && existing.IsLocked && existing.ApartmentId != apt.ApartmentId)
        {
            issues.Add(LockedIssue(contract, existing));
        }

        if (contract.FloorPlanId != apt.FloorPlanId)
        {
            issues.Add(Issue(ValidationIssue.Codes.FloorPlanMismatch, ValidationIssue.Severities.Error,
                $"Contracted plan {contract.FloorPlanCode} does not match apartment {apt.ApartmentNumber} ({apt.FloorPlanCode}).",
                contractId: contractId, tenantCode: contract.TenantCode,
                apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber));
        }

        // Capacity of destination after the move (exclude this contract if already there).
        var occupants = state.OccupantsOf(apt.ApartmentId)
            .Where(a => a.ContractId != contractId)
            .ToList();
        if (occupants.Count + 1 > apt.EffectiveCapacity)
        {
            issues.Add(Issue(ValidationIssue.Codes.CapacityExceeded, ValidationIssue.Severities.Error,
                $"Apartment {apt.ApartmentNumber} effective capacity is {apt.EffectiveCapacity}; move would place {occupants.Count + 1}.",
                contractId: contractId, tenantCode: contract.TenantCode,
                apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber));
        }

        AddRestrictionIssues(state, contract, apt, occupants, issues);
        AddAllergyWarning(state, contract, apt, occupants, issues);

        return issues;
    }

    /// <summary>
    /// Validate an atomic swap of two assigned contracts. Both resulting apartments are checked.
    /// </summary>
    public IReadOnlyList<ValidationIssue> ValidateSwap(DraftState state, uint contractIdA, uint contractIdB)
    {
        var issues = new List<ValidationIssue>();
        AddDraftEditable(state, issues);
        if (issues.Count > 0) return issues;

        if (contractIdA == contractIdB)
        {
            issues.Add(Issue(ValidationIssue.Codes.SwapSameContract, ValidationIssue.Severities.Error,
                "A swap requires two different contracts.", contractId: contractIdA));
            return issues;
        }

        if (!state.Contracts.TryGetValue(contractIdA, out var ca) ||
            !state.Contracts.TryGetValue(contractIdB, out var cb))
        {
            if (!state.Contracts.ContainsKey(contractIdA))
                issues.Add(Issue(ValidationIssue.Codes.ContractNotFound, ValidationIssue.Severities.Error,
                    $"Contract {contractIdA} was not found.", contractId: contractIdA));
            if (!state.Contracts.ContainsKey(contractIdB))
                issues.Add(Issue(ValidationIssue.Codes.ContractNotFound, ValidationIssue.Severities.Error,
                    $"Contract {contractIdB} was not found.", contractId: contractIdB));
            return issues;
        }

        if (!state.AssignmentsByContract.TryGetValue(contractIdA, out var aa) ||
            !state.AssignmentsByContract.TryGetValue(contractIdB, out var ab))
        {
            issues.Add(Issue(ValidationIssue.Codes.SwapRequiresBothAssigned, ValidationIssue.Severities.Error,
                "Both tenants must already be assigned to swap apartments.",
                contractId: contractIdA, tenantCode: ca.TenantCode,
                relatedContractId: contractIdB, relatedTenantCode: cb.TenantCode));
            return issues;
        }

        if (aa.ApartmentId == ab.ApartmentId)
        {
            // No-op swap within same apartment — treat as success with no issues.
            return issues;
        }

        if (aa.IsLocked)
            issues.Add(LockedIssue(ca, aa));
        if (ab.IsLocked)
            issues.Add(LockedIssue(cb, ab));

        // Simulate: A→B's apartment, B→A's apartment, then validate each destination.
        var simulated = state.Clone();
        simulated.AssignmentsByContract[contractIdA] = aa with { ApartmentId = ab.ApartmentId };
        simulated.AssignmentsByContract[contractIdB] = ab with { ApartmentId = aa.ApartmentId };

        // Validate resulting placements without re-checking draft editability / source locks
        // (locks already checked; destinations are swapping so source locks are the only locks).
        issues.AddRange(ValidatePlacementOnly(simulated, ca, ab.ApartmentId)
            .Where(i => i.Code is not ValidationIssue.Codes.AssignmentLocked
                and not ValidationIssue.Codes.DraftNotEditable));
        issues.AddRange(ValidatePlacementOnly(simulated, cb, aa.ApartmentId)
            .Where(i => i.Code is not ValidationIssue.Codes.AssignmentLocked
                and not ValidationIssue.Codes.DraftNotEditable));

        return Dedup(issues);
    }

    public IReadOnlyList<ValidationIssue> ValidateLock(DraftState state, uint contractId, bool locked, string? lockedBy)
    {
        var issues = new List<ValidationIssue>();
        AddDraftEditable(state, issues);
        if (issues.Count > 0) return issues;

        if (!state.Contracts.TryGetValue(contractId, out var contract))
        {
            issues.Add(Issue(ValidationIssue.Codes.ContractNotFound, ValidationIssue.Severities.Error,
                $"Contract {contractId} was not found.", contractId: contractId));
            return issues;
        }

        if (!state.AssignmentsByContract.TryGetValue(contractId, out _))
        {
            issues.Add(Issue(ValidationIssue.Codes.NotAssigned, ValidationIssue.Severities.Error,
                $"Contract {contractId} ({contract.TenantCode}) has no assignment to lock or unlock.",
                contractId: contractId, tenantCode: contract.TenantCode));
            return issues;
        }

        if (locked && string.IsNullOrWhiteSpace(lockedBy))
        {
            issues.Add(Issue(ValidationIssue.Codes.LockOwnerRequired, ValidationIssue.Severities.Error,
                "Locking an assignment requires an explicit operator identity (fictional staff name).",
                contractId: contractId, tenantCode: contract.TenantCode));
        }

        return issues;
    }

    /// <summary>
    /// Full-draft scan: capacity, restriction conflicts on current placements, advisory warnings.
    /// </summary>
    public IReadOnlyList<ValidationIssue> ValidateDraft(DraftState state)
    {
        var issues = new List<ValidationIssue>();

        foreach (var apt in state.Apartments.Values.Where(a => a.IsActive))
        {
            var occ = state.OccupantsOf(apt.ApartmentId).ToList();
            if (occ.Count > apt.EffectiveCapacity)
            {
                issues.Add(Issue(ValidationIssue.Codes.CapacityExceeded, ValidationIssue.Severities.Error,
                    $"Apartment {apt.ApartmentNumber} has {occ.Count} occupants but effective capacity {apt.EffectiveCapacity}.",
                    apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber));
            }
        }

        foreach (var assignment in state.AssignmentsByContract.Values)
        {
            if (!state.Contracts.TryGetValue(assignment.ContractId, out var contract)) continue;
            if (!state.Apartments.TryGetValue(assignment.ApartmentId, out var apt)) continue;

            if (contract.FloorPlanId != apt.FloorPlanId)
            {
                issues.Add(Issue(ValidationIssue.Codes.FloorPlanMismatch, ValidationIssue.Severities.Error,
                    $"Contract {contract.TenantCode} is in {apt.ApartmentNumber} ({apt.FloorPlanCode}) but contracted {contract.FloorPlanCode}.",
                    contractId: contract.ContractId, tenantCode: contract.TenantCode,
                    apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber));
            }

            if (!apt.IsActive)
            {
                issues.Add(Issue(ValidationIssue.Codes.ApartmentInactive, ValidationIssue.Severities.Error,
                    $"Contract {contract.TenantCode} is assigned to inactive apartment {apt.ApartmentNumber}.",
                    contractId: contract.ContractId, tenantCode: contract.TenantCode,
                    apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber));
            }

            var others = state.OccupantsOf(apt.ApartmentId)
                .Where(a => a.ContractId != contract.ContractId).ToList();
            AddRestrictionIssues(state, contract, apt, others, issues);
            AddAllergyWarning(state, contract, apt, others, issues);
        }

        foreach (var req in state.RoommateRequests.Where(r =>
                     r.Status is "pending" or "unresolved" or "ambiguous" or "excluded"))
        {
            issues.Add(Issue(ValidationIssue.Codes.UnresolvedRoommateRequest, ValidationIssue.Severities.Info,
                $"Roommate request status '{req.Status}' for {req.TenantCode}: \"{Truncate(req.RawText, 80)}\"" +
                (string.IsNullOrEmpty(req.ExclusionReason) ? "" : $" ({req.ExclusionReason})"),
                contractId: req.ContractId, tenantCode: req.TenantCode));
        }

        return Dedup(issues);
    }

    private IReadOnlyList<ValidationIssue> ValidatePlacementOnly(DraftState state, ContractState contract, uint apartmentId)
    {
        // Reuse move validation against simulated state (null existing lock check handled by caller).
        return ValidateMove(state, contract.ContractId, apartmentId);
    }

    private static void AddDraftEditable(DraftState state, List<ValidationIssue> issues)
    {
        if (state.IsEditable) return;
        issues.Add(Issue(ValidationIssue.Codes.DraftNotEditable, ValidationIssue.Severities.Error,
            $"Draft {state.DraftId} has status '{state.Status}' and is not editable as a working draft."));
    }

    private static void AddRestrictionIssues(
        DraftState state,
        ContractState contract,
        ApartmentSnapshot apt,
        List<AssignmentState> otherOccupants,
        List<ValidationIssue> issues)
    {
        foreach (var r in state.Restrictions.Where(x => x.ContractId == contract.ContractId))
        {
            switch (r.RestrictionType)
            {
                case "required_apartment" when r.ApartmentId is uint required && required != apt.ApartmentId:
                    var requiredNum = state.Apartments.TryGetValue(required, out var ra) ? ra.ApartmentNumber : required.ToString();
                    issues.Add(Issue(ValidationIssue.Codes.RestrictionRequiredApartment, ValidationIssue.Severities.Error,
                        $"Staff restriction requires apartment {requiredNum}; destination is {apt.ApartmentNumber}.",
                        contractId: contract.ContractId, tenantCode: contract.TenantCode,
                        apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber));
                    break;

                case "excluded_apartment" when r.ApartmentId is uint excluded && excluded == apt.ApartmentId:
                    issues.Add(Issue(ValidationIssue.Codes.RestrictionExcludedApartment, ValidationIssue.Severities.Error,
                        $"Staff restriction excludes apartment {apt.ApartmentNumber}.",
                        contractId: contract.ContractId, tenantCode: contract.TenantCode,
                        apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber));
                    break;

                case "required_floor" when r.Value is string rf &&
                                          (!int.TryParse(rf, out var reqFloor) || apt.Floor != reqFloor):
                    issues.Add(Issue(ValidationIssue.Codes.RestrictionRequiredFloor, ValidationIssue.Severities.Error,
                        $"Staff restriction requires floor {r.Value}; apartment {apt.ApartmentNumber} is on floor {apt.Floor}.",
                        contractId: contract.ContractId, tenantCode: contract.TenantCode,
                        apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber));
                    break;

                case "excluded_floor" when r.Value is string ef &&
                                          int.TryParse(ef, out var exclFloor) && apt.Floor == exclFloor:
                    issues.Add(Issue(ValidationIssue.Codes.RestrictionExcludedFloor, ValidationIssue.Severities.Error,
                        $"Staff restriction excludes floor {exclFloor}; apartment {apt.ApartmentNumber} is on that floor.",
                        contractId: contract.ContractId, tenantCode: contract.TenantCode,
                        apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber));
                    break;

                case "must_not_share_with" when r.OtherTenantId is uint otherTenantId:
                    foreach (var other in otherOccupants)
                    {
                        if (!state.Contracts.TryGetValue(other.ContractId, out var oc)) continue;
                        if (oc.TenantId != otherTenantId) continue;
                        issues.Add(Issue(ValidationIssue.Codes.RestrictionMustNotShare, ValidationIssue.Severities.Error,
                            $"Staff restriction: {contract.TenantCode} must not share with {oc.TenantCode}.",
                            contractId: contract.ContractId, tenantCode: contract.TenantCode,
                            apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber,
                            relatedContractId: oc.ContractId, relatedTenantCode: oc.TenantCode));
                    }
                    break;
            }
        }

        // Also check reverse must_not_share_with held by current occupants against this contract's tenant.
        foreach (var other in otherOccupants)
        {
            foreach (var r in state.Restrictions.Where(x =>
                         x.ContractId == other.ContractId &&
                         x.RestrictionType == "must_not_share_with" &&
                         x.OtherTenantId == contract.TenantId))
            {
                if (!state.Contracts.TryGetValue(other.ContractId, out var oc)) continue;
                issues.Add(Issue(ValidationIssue.Codes.RestrictionMustNotShare, ValidationIssue.Severities.Error,
                    $"Staff restriction: {oc.TenantCode} must not share with {contract.TenantCode}.",
                    contractId: contract.ContractId, tenantCode: contract.TenantCode,
                    apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber,
                    relatedContractId: oc.ContractId, relatedTenantCode: oc.TenantCode));
            }
        }
    }

    private static void AddAllergyWarning(
        DraftState state,
        ContractState contract,
        ApartmentSnapshot apt,
        List<AssignmentState> otherOccupants,
        List<ValidationIssue> issues)
    {
        var self = state.Preferences.FirstOrDefault(p => p.ContractId == contract.ContractId);
        if (self is null) return;

        foreach (var other in otherOccupants)
        {
            var op = state.Preferences.FirstOrDefault(p => p.ContractId == other.ContractId);
            if (op is null) continue;
            if (!state.Contracts.TryGetValue(other.ContractId, out var oc)) continue;

            var conflict =
                (self.HasAnimal && op.AnimalAllergyRestriction) ||
                (self.AnimalAllergyRestriction && op.HasAnimal);
            if (!conflict) continue;

            issues.Add(Issue(ValidationIssue.Codes.UnconfirmedAllergyColocation, ValidationIssue.Severities.Warning,
                $"Legacy preference flags suggest animal/allergy co-location for {contract.TenantCode} and {oc.TenantCode} in {apt.ApartmentNumber}. Not enforced: no staff-confirmation fields in schema.",
                contractId: contract.ContractId, tenantCode: contract.TenantCode,
                apartmentId: apt.ApartmentId, apartmentNumber: apt.ApartmentNumber,
                relatedContractId: oc.ContractId, relatedTenantCode: oc.TenantCode));
        }
    }

    private static ValidationIssue LockedIssue(ContractState contract, AssignmentState assignment) =>
        Issue(ValidationIssue.Codes.AssignmentLocked, ValidationIssue.Severities.Error,
            $"Assignment for {contract.TenantCode} is locked by '{assignment.LockedBy}' and cannot be changed.",
            contractId: contract.ContractId, tenantCode: contract.TenantCode,
            apartmentId: assignment.ApartmentId);

    private static ValidationIssue Issue(
        string code,
        string severity,
        string message,
        uint? contractId = null,
        string? tenantCode = null,
        uint? apartmentId = null,
        string? apartmentNumber = null,
        uint? relatedContractId = null,
        string? relatedTenantCode = null) => new()
    {
        Code = code,
        Severity = severity,
        Message = message,
        ContractId = contractId,
        TenantCode = tenantCode,
        ApartmentId = apartmentId,
        ApartmentNumber = apartmentNumber,
        RelatedContractId = relatedContractId,
        RelatedTenantCode = relatedTenantCode,
    };

    private static List<ValidationIssue> Dedup(List<ValidationIssue> issues) =>
        issues
            .GroupBy(i => (i.Code, i.ContractId, i.ApartmentId, i.RelatedContractId, i.Message))
            .Select(g => g.First())
            .ToList();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
