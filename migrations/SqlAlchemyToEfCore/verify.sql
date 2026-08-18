-- =============================================================================
-- verify.sql
--
-- Post-migration verification: emit per-table row counts, FK orphan
-- counts, and a deterministic business checksum. Returns a single
-- result set with one row per business table:
--
--   table_name        text
--   row_count         bigint
--   orphan_count      bigint
--   business_checksum text
--
-- The orphan count is computed per uuid-typed FK column by counting
-- rows where the FK is non-null but the parent's guid_id does not
-- exist. The business checksum is md5(concat_ws('|', col1, col2, ...))
-- per row, aggregated across all rows in id order.
--
-- Implementation: PL/pgSQL function that uses dynamic SQL (EXECUTE
-- format(...)) to compose per-table queries, since the column list
-- and FK references are not known at script-authoring time.
-- =============================================================================

CREATE OR REPLACE FUNCTION _ontopilot_resolve_parent(child_table text, fk_col text)
    RETURNS text AS $$
DECLARE
    parent text;
BEGIN
    SELECT ccu.table_name INTO parent
    FROM information_schema.table_constraints tc
    JOIN information_schema.key_column_usage kcu
      ON tc.constraint_name = kcu.constraint_name
     AND tc.table_schema = kcu.table_schema
    JOIN information_schema.constraint_column_usage ccu
      ON ccu.constraint_name = tc.constraint_name
     AND ccu.table_schema = tc.table_schema
    WHERE tc.constraint_type = 'FOREIGN KEY'
      AND tc.table_schema = 'public'
      AND tc.table_name = child_table
      AND kcu.column_name = fk_col
    LIMIT 1;
    RETURN parent;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION _ontopilot_business_columns(t text)
    RETURNS text AS $$
DECLARE
    cols text;
    fk_cols text[] := ARRAY[
        'userid', 'knowledgesystemid', 'updatedbyid', 'createdbyid',
        'llmproviderid', 'embeddingproviderid', 'ownerid', 'documentid',
        'chunkid', 'jobid', 'auditeventid', 'actorid', 'releaseid',
        'sourcechunkid', 'extractionjobid'
    ];
BEGIN
    SELECT string_agg(format('%I', c.column_name), ', ' ORDER BY c.ordinal_position)
        INTO cols
    FROM information_schema.columns c
    WHERE c.table_schema = 'public'
      AND c.table_name = t
      AND c.column_name NOT IN ('guid_id', 'legacy_id')
      AND NOT (c.column_name = ANY(fk_cols) AND c.data_type = 'uuid');
    RETURN cols;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION _ontopilot_verify() RETURNS TABLE(
    table_name text,
    row_count bigint,
    orphan_count bigint,
    business_checksum text
) AS $$
DECLARE
    t text;
    fk_cols text[] := ARRAY[
        'userid', 'knowledgesystemid', 'updatedbyid', 'createdbyid',
        'llmproviderid', 'embeddingproviderid', 'ownerid', 'documentid',
        'chunkid', 'jobid', 'auditeventid', 'actorid', 'releaseid',
        'sourcechunkid', 'extractionjobid'
    ];
    fk_col text;
    parent_table text;
    cols text;
    rc bigint;
    oc bigint;
    cs text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'users', 'authsession', 'ksgrant', 'document', 'chunk', 'knowledgesystem',
        'knowledgepromptoverride', 'knowledgeapitoken', 'mcpusertoken', 'provider',
        'systemconfig', 'extractionjob', 'axiomprovenance', 'aboxprovenance',
        'auditevent', 'ontologyrelease', 'releasedeployment',
        'releasestatementprovenance', 'exportjob', 'conflict', 'entityresolution',
        'termproposal', 'tboxreconciliation', 'validationdecision'
    ] LOOP
        EXECUTE format('SELECT count(*)::bigint FROM %I', t) INTO rc;

        oc := 0;
        FOR fk_col IN
            SELECT c.column_name
            FROM information_schema.columns c
            WHERE c.table_schema = 'public'
              AND c.table_name = t
              AND c.data_type = 'uuid'
              AND c.column_name = ANY(fk_cols)
        LOOP
            parent_table := _ontopilot_resolve_parent(t, fk_col);
            IF parent_table IS NULL THEN
                CONTINUE;
            END IF;
            EXECUTE format(
                'SELECT count(*)::bigint FROM %I t WHERE t.%I IS NOT NULL '
                'AND NOT EXISTS (SELECT 1 FROM %I p WHERE p.guid_id = t.%I)',
                t, fk_col, parent_table, fk_col
            ) INTO oc;
            -- oc is the count for this FK only; accumulate below.
            -- (We need a separate variable since oc is also being used
            --  as the accumulator for the table total.)
        END LOOP;
        -- Sum across FKs: re-iterate with an accumulator.
        oc := 0;
        FOR fk_col IN
            SELECT c.column_name
            FROM information_schema.columns c
            WHERE c.table_schema = 'public'
              AND c.table_name = t
              AND c.data_type = 'uuid'
              AND c.column_name = ANY(fk_cols)
        LOOP
            parent_table := _ontopilot_resolve_parent(t, fk_col);
            IF parent_table IS NULL THEN
                CONTINUE;
            END IF;
            DECLARE
                sub_oc bigint;
            BEGIN
                EXECUTE format(
                    'SELECT count(*)::bigint FROM %I t WHERE t.%I IS NOT NULL '
                    'AND NOT EXISTS (SELECT 1 FROM %I p WHERE p.guid_id = t.%I)',
                    t, fk_col, parent_table, fk_col
                ) INTO sub_oc;
                oc := oc + COALESCE(sub_oc, 0);
            END;
        END LOOP;

        cols := _ontopilot_business_columns(t);
        IF cols IS NOT NULL AND cols <> '' THEN
            EXECUTE format(
                'SELECT COALESCE(string_agg(md5(row_concat), ''''), '''') FROM ('
                'SELECT concat_ws(''|'', %s) AS row_concat FROM %I ORDER BY id) r',
                cols, t
            ) INTO cs;
        ELSE
            cs := '';
        END IF;

        table_name := t;
        row_count := rc;
        orphan_count := oc;
        business_checksum := cs;
        RETURN NEXT;
    END LOOP;
END;
$$ LANGUAGE plpgsql;

SELECT * FROM _ontopilot_verify();
