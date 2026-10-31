-- Sanity checks for a loaded seed. Read-only.
SELECT 'tenants' k, COUNT(*) v FROM tenants
UNION ALL SELECT 'contracts', COUNT(*) FROM contracts
UNION ALL SELECT 'apartments', COUNT(*) FROM apartments
UNION ALL SELECT 'roommate_requests', COUNT(*) FROM roommate_requests
UNION ALL SELECT 'placement_restrictions', COUNT(*) FROM placement_restrictions
UNION ALL SELECT 'locked_assignments', COUNT(*) FROM assignments WHERE is_locked
UNION ALL SELECT 'renewing', COUNT(*) FROM contracts WHERE tenant_type='renewing'
UNION ALL SELECT 'renewing_stay', COUNT(*) FROM contracts WHERE stay_preference='stay'
UNION ALL SELECT 'renewing_move', COUNT(*) FROM contracts WHERE stay_preference='move'
UNION ALL SELECT 'has_animal', COUNT(*) FROM preferences WHERE has_animal
UNION ALL SELECT 'allergy_restriction', COUNT(*) FROM preferences WHERE animal_allergy_restriction
UNION ALL SELECT 'total_capacity', SUM(effective_capacity) FROM v_apartment_capacity;

-- Demand vs. capacity per floor plan (negative spare = oversubscribed)
SELECT fp.code, fp.resident_capacity AS cap_per_apt,
       (SELECT COUNT(*) FROM apartments a WHERE a.floor_plan_id = fp.floor_plan_id) AS apartments,
       (SELECT COALESCE(SUM(effective_capacity),0) FROM v_apartment_capacity v WHERE v.floor_plan_id = fp.floor_plan_id) AS capacity,
       (SELECT COUNT(*) FROM contracts c WHERE c.floor_plan_id = fp.floor_plan_id) AS contracts,
       (SELECT COALESCE(SUM(effective_capacity),0) FROM v_apartment_capacity v WHERE v.floor_plan_id = fp.floor_plan_id)
     - (SELECT COUNT(*) FROM contracts c WHERE c.floor_plan_id = fp.floor_plan_id) AS spare
FROM floor_plans fp ORDER BY fp.floor_plan_id;

-- Renewals whose current apartment's plan differs from the contracted plan
SELECT t.tenant_code, a.apartment_number, fpo.code AS current_plan, fpn.code AS contracted_plan, c.stay_preference
FROM contracts c JOIN tenants t USING (tenant_id)
JOIN apartments a ON a.apartment_id = c.current_apartment_id
JOIN floor_plans fpo ON fpo.floor_plan_id = a.floor_plan_id
JOIN floor_plans fpn ON fpn.floor_plan_id = c.floor_plan_id
WHERE a.floor_plan_id <> c.floor_plan_id;

-- Current occupancy (renewals) never exceeds effective capacity
SELECT COUNT(*) AS apartments_over_capacity_by_renewals FROM (
  SELECT v.apartment_id FROM v_apartment_capacity v JOIN contracts c ON c.current_apartment_id = v.apartment_id
  GROUP BY v.apartment_id, v.effective_capacity HAVING COUNT(*) > MAX(v.effective_capacity)) x;

-- Roommate request texts that match no tenant's "First Last" exactly (case-insensitive)
SELECT COUNT(*) AS requests_without_exact_name_match FROM roommate_requests r
WHERE NOT EXISTS (SELECT 1 FROM tenants t WHERE LOWER(CONCAT(t.first_name,' ',t.last_name)) = LOWER(r.raw_text)
                                             OR LOWER(CONCAT(t.last_name,', ',t.first_name)) = LOWER(r.raw_text));

-- Locked assignments
SELECT t.tenant_code, a.apartment_number, s.is_locked, LEFT(s.explanation, 60) AS explanation
FROM assignments s JOIN contracts c USING (contract_id) JOIN tenants t USING (tenant_id) JOIN apartments a ON a.apartment_id = s.apartment_id;
