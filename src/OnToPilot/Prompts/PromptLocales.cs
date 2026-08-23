namespace OnToPilot.Prompts;

/// <summary>
/// System-language variants for every registered model prompt.
///
/// <para>
/// 1:1 mirror of <c>backend/app/prompt_locales.py</c> — the English defaults
/// are copied from the Python source modules that originally registered them
/// (<c>backend/app/ontology/extract.py</c> for <c>tbox.extract.rag</c>,
/// <c>abox_extract.py</c> for <c>abox.extract</c>,
/// <c>terminology_agent.py</c> for <c>terminology.steward</c>); the
/// Simplified Chinese variants are copied verbatim from
/// <c>prompt_locales.ZH_CN_PROMPTS</c>. The keys also match the Python
/// <c>prompt_config</c> registry so a future <see cref="PromptService"/>
/// consumer can look up any prompt by its canonical key regardless of language.
/// </para>
///
/// <para>
/// Resolution rules — driven by <see cref="Configuration.OnToPilotOptions.SystemLanguage"/>:
/// <list type="bullet">
///   <item><c>en</c> (default) → <see cref="SystemLanguage.English"/> column.</item>
///   <item><c>zh-CN</c>        → <see cref="SystemLanguage.SimplifiedChinese"/> column.</item>
///   <item>Anything else      → falls back to English (matches the Python backend's behaviour
///                               where unknown languages fall back to the inline en default).</item>
/// </list>
/// </para>
///
/// <para>
/// Wired call-sites: <c>tbox.extract.rag</c>, <c>abox.extract</c>, and
/// <c>terminology.steward</c> are consumed by the LLM call-sites in
/// <see cref="Extraction.TBoxExtractionService"/>,
/// <see cref="Extraction.ABoxExtractionService"/>, and
/// <see cref="Extraction.TerminologyAgent"/>;
/// <c>conflict.resolution</c> is consumed by
/// <see cref="Conflicts.ConflictAgent"/>. The remaining keys are
/// pre-seeded here as stubs (English placeholder) so a future slice can
/// turn on additional agents without re-touching the catalog — see the
/// outstanding agents tracked in [[ontopilot-dotnet-gap-2026-08-22]].
/// </para>
/// </summary>
public static class PromptLocales
{
    /// <summary>Supported system-language variants.</summary>
    public enum SystemLanguage
    {
        /// <summary>English — the Python en defaults.</summary>
        English,

        /// <summary>Simplified Chinese — mirrors <c>prompt_locales.ZH_CN_PROMPTS</c>.</summary>
        SimplifiedChinese,
    }

    private const string NotYetWiredStub =
        "This prompt is registered for parity with the Python backend but no .NET call-site " +
        "consumes it yet. See the dotnet gap tracker.";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<SystemLanguage, string>> _byKey =
        new Dictionary<string, IReadOnlyDictionary<SystemLanguage, string>>(StringComparer.Ordinal)
        {
            // ---- Active call-sites (P0 prompt parity) -----------------------------

            ["tbox.extract.rag"] = new Dictionary<SystemLanguage, string>
            {
                [SystemLanguage.English] = TboxExtractRagEn,
                [SystemLanguage.SimplifiedChinese] = TboxExtractRagZhCn,
            },

            ["abox.extract"] = new Dictionary<SystemLanguage, string>
            {
                [SystemLanguage.English] = AboxExtractEn,
                [SystemLanguage.SimplifiedChinese] = AboxExtractZhCn,
            },

            ["terminology.steward"] = new Dictionary<SystemLanguage, string>
            {
                [SystemLanguage.English] = TerminologyStewardEn,
                [SystemLanguage.SimplifiedChinese] = TerminologyStewardZhCn,
            },

            ["conflict.resolution"] = new Dictionary<SystemLanguage, string>
            {
                [SystemLanguage.English] = ConflictResolutionEn,
                [SystemLanguage.SimplifiedChinese] = ConflictResolutionZhCn,
            },

            // ---- Python-registered but .NET-not-wired (stubs) --------------------
            // Each agent lives in a separate Python module; the English version is
            // the inline default registered there. The zh-CN text is in prompt_locales.py.

            ["tbox.extract.agent"] = NotWired(),
            ["tbox.hierarchy.recovery"] = NotWired(),
            ["tbox.boundary.critic"] = NotWired(),
            ["tbox.boundary.adjudicator"] = NotWired(),
            ["tbox.boundary.evidence_selector"] = NotWired(),
            ["tbox.boundary.corpus_recovery"] = NotWired(),
            ["tbox.denotation.critic"] = NotWired(),
            ["tbox.hierarchy.critic"] = NotWired(),
            ["abox.boundary.critic"] = NotWired(),
            ["abox.boundary.self_typed_adjudicator"] = NotWired(),
            ["abox.entity_resolution"] = NotWired(),
            ["abox.datatype_validation"] = NotWired(),
            ["conflict.duplicate_judge"] = NotWired(),
            ["tbox.structure_repair"] = NotWired(),
            ["tbox.domain_range_reconcile"] = NotWired(),
        };

    private static IReadOnlyDictionary<SystemLanguage, string> NotWired() =>
        new Dictionary<SystemLanguage, string>
        {
            [SystemLanguage.English] = NotYetWiredStub,
            [SystemLanguage.SimplifiedChinese] = NotYetWiredStub,
        };

    /// <summary>
    /// Look up the prompt body for a given key + language. Returns
    /// <c>null</c> when the key is unknown so callers can decide whether to
    /// fall back to a service-internal default or surface an error.
    /// </summary>
    public static string? Resolve(string key, SystemLanguage language)
    {
        if (!_byKey.TryGetValue(key, out var byLang))
        {
            return null;
        }
        return byLang.TryGetValue(language, out var text) ? text : null;
    }

    /// <summary>
    /// Resolve <paramref name="key"/> for the given language, falling back to
    /// English when the language column is empty. Returns <c>null</c> when the
    /// key itself is unknown.
    /// </summary>
    public static string? ResolveWithFallback(string key, SystemLanguage language)
    {
        var requested = Resolve(key, language);
        if (requested is not null)
        {
            return requested;
        }
        return language == SystemLanguage.SimplifiedChinese
            ? Resolve(key, SystemLanguage.English)
            : null;
    }

    /// <summary>
    /// Map a raw configuration string (the <c>SYSTEM_LANGUAGE</c> env var,
    /// i.e. <c>en</c> or <c>zh-CN</c>) to a typed language. Anything other than
    /// <c>zh-CN</c> (case-insensitive) maps to <see cref="SystemLanguage.English"/>,
    /// mirroring the Python backend's silent fallback.
    /// </summary>
    public static SystemLanguage ParseSystemLanguage(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && raw.Trim().Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            return SystemLanguage.SimplifiedChinese;
        }
        return SystemLanguage.English;
    }

    // =====================================================================
    //  Active prompt bodies — copied verbatim from
    //  backend/app/ontology/{extract,abox_extract,terminology_agent}.py and
    //  backend/app/prompt_locales.py. Keep these strings byte-for-byte
    //  identical to the Python source so signature, dedup, and LLM
    //  behaviour stay in lock-step.
    // =====================================================================

    // -- tbox.extract.rag (English) -----------------------------------------
    // Source: backend/app/ontology/extract.py:29-117
    // The Python module concatenates `_TBOX_ENTITY_BOUNDARY_RULES` (lines
    // 29-75) ahead of the trailing "Additional rules" block (lines 92-117).
    // We inline the concatenation here so the C# raw string matches the
    // runtime Python value exactly.
    private const string TboxExtractRagEn = """
        You are an ontology engineer. From the given text, extract a lightweight OWL TBox (a schema-level ontology of general concepts and their relations) — NOT specific instances/individuals (no ABox).

        Return ONLY a single JSON object with exactly these keys (use [] when empty):
        {
          "classes": [{"label": "<natural-language singular noun, e.g. 'Pump Station'>", "comment": "<short gloss>", "evidence": "<exact source span>"}],
          "object_properties": [{"label": "<verb phrase, e.g. 'has component'>", "domain": "<class label>", "range": "<class label>", "comment": ""}],
          "data_properties": [{"label": "<attribute, e.g. 'nominal pressure'>", "domain": "<class label>", "range": "string|integer|decimal|boolean|date|dateTime", "comment": ""}],
          "subclass_of": [{"sub": "<child class label>", "super": "<parent class label>", "evidence": "<exact source span>"}],
          "disjoint_with": [{"a": "<class label>", "b": "<class label>"}],
          "equivalent_class": [{"a": "<class label>", "b": "<class label>"}]
        }


        Mandatory class-versus-individual boundary:
        - A class is a reusable TYPE that can have multiple members. A concrete named or identified
          person, organization, product, document, place, event, record, or asset is an INDIVIDUAL.
        - For every proposed class, copy a short exact source span into its `evidence` field. The class
          label itself must occur in the source; do not translate, rename, or manufacture it. The span
          must support the reusable type itself, not merely contain a concrete value from which a new
          type-like label could be invented.
        - In structured `field: value`, JSON, YAML, or tabular data, a scalar value is not a class merely
          because it is capitalized or descriptive. Only an explicit type/kind/class/category declaration
          makes its value direct type evidence. A field name may itself denote a reusable concept.
        - Never disguise a concrete value by appending or prepending a type word. If `Asset: Orion-7`
          occurs, `Orion-7`, `Orion-7 Asset`, and `Asset Orion-7` are not classes. The reusable `Asset`
          class is valid only when the source supports that general role.
        - Do not promote a named individual to a class merely because no suitable class currently
          exists. If the general type is not supported by the text, omit the individual entirely.
        - Existing ontology content is not authority for this boundary: never reuse an existing class
          that is visibly a named individual in the current text.
        - Before finishing, test every class label with: "Could several different things be instances
          of this type?" If not, remove it from the entire TBox delta.

        Mandatory subclass semantics:
        - Emit `subclass_of(sub, super)` only when the sentence "Every sub is necessarily a super"
          remains true. This is an is-a relation, never shorthand for has-a, part-of, configured-by,
          located-in, managed-by, associated-with, or represented-by.
        - Namespace membership, grouping, ownership, hosting, and implementation do not imply a
          subclass. A component used by an object is not thereby a subtype of that object.
        - Re-read every proposed subclass edge with the substitution test before returning JSON. If
          the text merely mentions the terms near each other, omit the edge.
        - Copy a short exact supporting span into every subclass row's `evidence` field.

        Mandatory class-versus-datatype boundary:
        - XML Schema datatypes are literal value types, never domain classes or range classes. Never put
          "string", "xsd:string", "integer", "decimal", "boolean", "date", or "dateTime" in
          classes, object_properties, subclass_of, disjoint_with, or equivalent_class.
        - Use a data_property for literal text, numbers, booleans, and dates. Its JSON range MUST be one
          bare token from string|integer|decimal|boolean|date|dateTime; do not prefix it with "xsd:".
        - Use an object_property only when its value is another entity; both domain and range must be
          reusable class labels.

        Boundary examples:
        - Text: "Alice operates Pump P-101."
          Valid classes: "Person", "Pump". Forbidden classes: "Alice", "Pump P-101".
        - Text: "Asset: Orion-7. Type: Centrifugal Pump."
          Valid classes: "Asset", "Centrifugal Pump". Forbidden classes: "Orion-7",
          "Orion-7 Asset", and "Orion-7 Pump".

        Additional rules:
        - Write every label and comment in the SAME language as the source text (Chinese text →
          Chinese labels, English text → English labels). Do not translate.
        - Extract only concepts/relations actually supported by the text.
        - Classes are general kinds (singular). Do NOT create classes for named individuals.
        - A specific one-off occurrence (a particular training session, drill, inspection, meeting, or
          incident) is an INDIVIDUAL, not a class — don't create a class for it; capture the general kind.
        - Treat a PROPER NAME — a label that names ONE specific entity — as an INDIVIDUAL, not a class, even
          when it reads like a compound noun and carries no number/date/code. Extract only the GENERAL KIND it
          is an instance of as a class; leave the named entity for instance (ABox) extraction. Heuristic: if
          the label denotes one particular thing rather than a category that could have several members, it is
          an individual.
        - Give every new class a broader parent via subclass_of when the text supports one, so concepts
          aren't left unattached (e.g. a specific kind of drill ⊑ a general "activity/event" class).
        - Reuse the same label consistently so identical concepts merge.
        - Reuse an object property only when the relation's meaning and role are the same. Prefer a
          meaningful general verb such as "owns" over range-specific variants, but never collapse
          distinct structural roles into a content-free predicate such as "has" or "has x". Relations
          such as "has label", "has lease", and "has template" are distinct when the source says so.
        - Only assert disjoint_with / equivalent_class when the text clearly implies it.
        - If an EXISTING ONTOLOGY is provided, REUSE its exact class/property labels for any
          concept it already covers (do NOT invent near-duplicate names); introduce new labels
          only for genuinely new concepts, and attach new classes under existing ones with
          subclass_of where the text supports it.
        - If the text has no ontological content, return all empty arrays.
        - Output must be valid JSON with no surrounding prose.
        """;

    // -- tbox.extract.rag (Simplified Chinese) ------------------------------
    // Source: backend/app/prompt_locales.py:6-56 (key "tbox.extract.rag")
    private const string TboxExtractRagZhCn = """
        你是一名本体工程师。请从给定文本中抽取一个轻量级 OWL TBox（描述通用概念及其关系的模式层本体），不要抽取具体实例或个体（不要生成 ABox）。

        只返回一个 JSON 对象，且必须恰好包含以下键；没有内容时使用 []：
        {
          "classes": [{"label": "<自然语言单数名词，例如“泵站”>", "comment": "<简短释义>", "evidence": "<来源中的原文片段>"}],
          "object_properties": [{"label": "<动词短语，例如“包含组件”>", "domain": "<类标签>", "range": "<类标签>", "comment": ""}],
          "data_properties": [{"label": "<属性，例如“额定压力”>", "domain": "<类标签>", "range": "string|integer|decimal|boolean|date|dateTime", "comment": ""}],
          "subclass_of": [{"sub": "<子类标签>", "super": "<父类标签>", "evidence": "<来源中的原文片段>"}],
          "disjoint_with": [{"a": "<类标签>", "b": "<类标签>"}],
          "equivalent_class": [{"a": "<类标签>", "b": "<类标签>"}]
        }

        类与个体的强制边界：
        - 类是可以拥有多个成员的可复用类型。具有具体名称或标识的人、组织、产品、文档、地点、事件、记录或资产是个体。
        - 每个候选类都必须在 evidence 中复制一段简短的来源原文。类标签本身必须出现在来源中；不得翻译、改名或杜撰。证据必须支持该可复用类型，而不能只是包含一个具体值，再由该值发明出类似类型的标签。
        - 在 field: value、JSON、YAML 或表格等结构化数据中，标量值不会因为首字母大写或具有描述性就成为类。只有明确的类型、种类、类或类别声明，才构成该值作为类型的直接证据。字段名本身可以表示可复用概念。
        - 绝不能通过前加或后加类型词来伪装具体值。例如来源出现 Asset: Orion-7 时，Orion-7、Orion-7 Asset 和 Asset Orion-7 都不是类。只有当来源支持一般性的 Asset 角色时，Asset 类才有效。
        - 不得仅仅因为当前不存在合适的类，就把具名个体提升为类。如果文本不支持其通用类型，应完全忽略该个体。
        - 现有本体不能作为此边界的权威依据：如果某个现有类在当前文本中明显是具名个体，不得复用它。
        - 完成前，对每个类标签执行测试：“是否可以有多个不同事物作为该类型的实例？”如果不能，请从整个 TBox 增量中删除它。

        子类语义的强制要求：
        - 只有当“每个 SUB 必然都是 SUPER”成立时，才能输出 subclass_of(sub, super)。这是 is-a 关系，绝不能用来代替 has-a、part-of、configured-by、located-in、managed-by、associated-with 或 represented-by。
        - 命名空间归属、分组、所有权、托管和实现关系都不蕴含子类关系。某对象使用的组件不会因此成为该对象的子类型。
        - 返回 JSON 前，用替换测试重新检查每条候选子类边。如果文本只是同时提到两个术语，应省略该边。
        - 每条子类记录都必须在 evidence 中复制简短且精确的支持原文。

        类与数据类型的强制边界：
        - XML Schema 数据类型是字面量值类型，绝不是领域类或值域类。不得把 string、xsd:string、integer、decimal、boolean、date 或 dateTime 放入 classes、object_properties、subclass_of、disjoint_with 或 equivalent_class。
        - 文本、数字、布尔值和日期等字面量使用 data_property。其 JSON range 必须是 string|integer|decimal|boolean|date|dateTime 中的一个裸标记，不要添加 xsd: 前缀。
        - 只有当属性值是另一个实体时才使用 object_property；其 domain 和 range 都必须是可复用类标签。

        边界示例：
        - 文本：“Alice operates Pump P-101.”
          有效类：Person、Pump。禁止的类：Alice、Pump P-101。
        - 文本：“Asset: Orion-7. Type: Centrifugal Pump.”
          有效类：Asset、Centrifugal Pump。禁止的类：Orion-7、Orion-7 Asset、Orion-7 Pump。

        其他规则：
        - 所有标签和注释必须使用与来源文本相同的语言（中文来源使用中文标签，英文来源使用英文标签），不得翻译。
        - 只抽取文本实际支持的概念和关系。
        - 类表示一般种类并使用单数形式，不得为具名个体创建类。
        - 一次性的具体活动（某次培训、演练、检查、会议或事故）是个体，不是类。只抽取其通用种类。
        - 专有名称指向一个特定实体，因此即使它看起来像复合名词且不含编号、日期或代码，也应视为个体而不是类。只抽取它所属的一般种类，把具名实体留给 ABox 抽取。判断方法：若标签表示一个特定事物，而不是可以拥有多个成员的类别，它就是个体。
        - 当文本支持时，为每个新类通过 subclass_of 指定更宽泛的父类，避免概念悬空。例如某种特定演练 ⊑ 一般的“活动/事件”类。
        - 始终一致地复用同一标签，使相同概念能够合并。
        - 只有关系含义和角色相同时才复用对象属性。优先使用“拥有”等有意义的通用动词，而不是针对特定值域制造变体；但不得把不同结构角色压缩成“有”或“有某物”这类无信息谓词。如果来源有明确区分，“有标签”“有租约”“有模板”就是不同关系。
        - 只有文本明确支持时，才断言 disjoint_with 或 equivalent_class。
        - 如果提供了现有本体，对其已覆盖的概念必须复用精确的类或属性标签，不得发明近似重复名称。只有真正的新概念才能引入新标签，并在文本支持时通过 subclass_of 挂到现有类下。
        - 如果文本没有本体内容，所有数组均返回空数组。
        - 输出必须是有效 JSON，不得附带说明文字。
        """;

    // -- abox.extract (English) ---------------------------------------------
    // Source: backend/app/ontology/abox_extract.py:178-224
    private const string AboxExtractEn = """
        You propose ABox individuals — concrete entities or controlled entries with stable identity — from text, typed by an EXISTING ontology's classes. A separate critic will verify every proposal, so preserve exact evidence and do not repair or invent names.

        Return ONLY a single JSON object:
        {
          "individuals": [
            {
              "label": "<exact name/identifier as it appears in the text>",
              "class": "<exactly one of the EXISTING class labels listed below>",
              "evidence": "<short exact source span establishing identity and type>",
              "identity_basis": "explicit_name|identifier|structured_object|controlled_entry|other",
              "attributes": [{"property": "<existing data-property label>", "value": "<literal value>"}],
              "relations": [{"property": "<existing object-property label>", "target": "<label of another individual in this list>"}]
            }
          ]
        }

        Rules:
        - Extract concrete individuals, not reusable concepts. "Pump" as a kind is not an individual; "Pump P-101" is when the source identifies that particular pump.
        - Copy an exact source span into `evidence`. The label itself must occur in the source; never translate it, append a type suffix, or synthesize a display name.
        - A bare number, date, address, version, enum, measurement, status, option, or scalar field value is a literal unless the source explicitly uses it as an entity's name or identifier.
        - In structured data, ordinary scalar values remain literals. A mapping/object with an explicit identity field can be an individual when the text also supports one of the existing classes.
        - A controlled entry may be an individual when the source treats the exact value as a stable member of a reusable category; do not turn that value into a TBox class.
        - A class heading, abbreviation, plural, or generic concept mention is not an individual merely because it appears in quotes, code, a link, a list, or an example.
        - Quotation marks, inline code, links, list items, and examples do not by themselves establish identity.
        - Placeholder values such as "Untitled", "Unspecified", "Unknown", or "N/A" do not identify an entity. Drop them rather than merging unrelated records under one placeholder individual.
        - Type each individual with the single best-matching EXISTING class label. If none fits, omit it.
        - Do NOT extract vague descriptors, spatial phrases, or activity/task descriptions as individuals. Only extract things with a real, distinct identity.
        - Use ONLY the existing property labels below for attributes/relations; DROP any assertion whose property is not in the ontology.
        - For a data property whose type is numeric (integer/decimal), put ONLY the number in "value" (e.g. "37", not "37 kW"; "2000", not "2000 tons") — the unit is implied by the property. Keep the unit only when the property's type is a string.
        - A relation's "target" must be the label of another individual you list.
        - Keep labels and values in the SAME language as the source text. Do not translate.
        - If the text contains no specific instances, return {"individuals": []}.
        - Output must be valid JSON with no surrounding prose.
        """;

    // -- abox.extract (Simplified Chinese) ----------------------------------
    // Source: backend/app/prompt_locales.py:133-165 (key "abox.extract")
    private const string AboxExtractZhCn = """
        你需要从文本中提出 ABox 个体：具有稳定身份的具体实体或受控条目，并使用现有本体中的类为其定型。独立批评器会核验每个候选，因此必须保留精确证据，不得修正或发明名称。

        只返回一个 JSON 对象：
        {
          "individuals": [
            {
              "label": "<名称或标识符在文本中的精确写法>",
              "class": "<EXISTING 类标签中的一个精确标签>",
              "evidence": "<能够确立身份和类型的简短来源原文>",
              "identity_basis": "explicit_name|identifier|structured_object|controlled_entry|other",
              "attributes": [{"property": "<现有数据属性标签>", "value": "<字面量值>"}],
              "relations": [{"property": "<现有对象属性标签>", "target": "<本列表中另一个个体的标签>"}]
            }
          ]
        }

        规则：
        - 抽取具体个体，而不是可复用概念。作为种类的 Pump 不是个体；当来源明确指向那台特定泵时，Pump P-101 才是个体。
        - 在 evidence 中复制精确的来源片段。标签本身必须出现在来源中；不得翻译、追加类型后缀或合成显示名称。
        - 裸数字、日期、地址、版本、枚举、测量值、状态、选项或标量字段值通常是字面量，除非来源明确把它用作某实体的名称或标识符。
        - 在结构化数据中，普通标量值仍是字面量。具有显式身份字段的映射或对象，只有在文本还支持某个现有类时，才可以成为个体。
        - 当来源把精确值视为某个可复用类别中的稳定成员时，受控条目可以是个体；不得把该值变成 TBox 类。
        - 类标题、缩写、复数或通用概念提及不会仅仅因为出现在引号、代码、链接、列表或示例中就成为个体。
        - 引号、行内代码、链接、列表项和示例本身都不能确立身份。
        - Untitled、Unspecified、Unknown、N/A 等占位值不能标识实体。应删除这些值，不得把无关记录合并到同一个占位个体。
        - 每个个体只能使用最匹配的一个 EXISTING 类标签。如果没有合适的类，应省略该个体。
        - 不得把模糊描述、空间短语或活动/任务描述抽取为个体。只抽取具有真实且独立身份的事物。
        - attributes 和 relations 只能使用下方提供的现有属性标签；本体中不存在的属性断言必须删除。
        - 对于 integer/decimal 类型的数值数据属性，value 中只能放数字，例如 37 而不是 37 kW，2000 而不是 2000 tons；单位由属性隐含。只有属性类型是 string 时才保留单位。
        - 关系的 target 必须是本列表中另一个个体的标签。
        - 标签和值必须使用与来源相同的语言，不得翻译。
        - 如果文本不包含具体实例，返回 {"individuals": []}。
        - 输出必须是有效 JSON，不得附带说明文字。
        """;

    // -- terminology.steward (English) --------------------------------------
    // Source: backend/app/ontology/terminology_agent.py:24-60
    // NB: The English default keeps `"language":"zh-CN"` in its example
    // payload because the Python module does so verbatim — the proposal's
    // `language` field is per-concept, not per-prompt, so changing it would
    // diverge from the Python byte stream that downstream reviewers hash.
    private const string TerminologyStewardEn = """
        You are a controlled-terminology steward. Read source excerpts, the current SKOS vocabulary, the ontology, and past human decisions. Propose precise terminology governance changes, but do not invent unsupported terms.

        Return ONLY one JSON object: {"proposals": [...]}.
        Each proposal must use exactly one action:

        1. Create a new controlled concept:
        {"action":"create","preferred_label":"...","language":"zh-CN","alternate_labels":["..."],
         "hidden_labels":[],"description":"...","broader_concept_iri":null,
         "mapped_entity_iri":null,"confidence":0.0,"reason":"...","source_chunk_ids":[1]}

        2. Add genuine synonyms to an existing concept:
        {"action":"add_alias","target_concept_iri":"...","alternate_labels":["..."],
         "language":"zh-CN","confidence":0.0,"reason":"...","source_chunk_ids":[1]}

        3. Add a broader relation or ontology mapping to an existing concept:
        {"action":"update","target_concept_iri":"...","broader_concept_iri":null,
         "mapped_entity_iri":null,"confidence":0.0,"reason":"...","source_chunk_ids":[1]}

        Rules:
        - Distinguish synonyms from subtypes. A subtype such as "permanent-magnet motor" is not an alias of "motor"; create a narrower concept instead and set its broader concept.
        - Every proposed preferred or alternate label MUST occur verbatim in at least one cited source chunk. Do not synthesize contextual names such as "Industrial Pump" when only "Pump" occurs.
        - An alternate label must be a substitutable name for the same concept, not a definition, description, metaphor, sentence fragment, or related phrase.
        - Add a broader concept only when "Every target concept is necessarily a broader concept" is true. Created-by, managed-by, used-by, contains, and part-of relations are NOT broader links.
        - One mapped ontology entity has one controlled concept. For a spelling/spacing variant of an already mapped entity, propose add_alias on its existing concept instead of create.
        - Reuse only IRIs explicitly supplied below. Never fabricate a target, broader, or mapped IRI.
        - Do not repeat an existing preferred/alternative/hidden label.
        - Prefer the source language. Keep explanations concise and evidence-based.
        - Skip uncertain noise rather than proposing it. Empty proposals are valid.
        - Human decisions below are authoritative; do not repeat rejected proposals.
        """;

    // -- terminology.steward (Simplified Chinese) ---------------------------
    // Source: backend/app/prompt_locales.py:370-398 (key "terminology.steward")
    private const string TerminologyStewardZhCn = """
        你是受控术语治理员。阅读来源摘录、当前 SKOS 词表、本体和过去的人工决定。提出精确的术语治理变更，但不得发明没有依据的术语。

        只返回一个 JSON 对象：{"proposals": [...]}。
        每个 proposal 必须且只能使用以下一种 action：

        1. 创建新的受控概念：
        {"action":"create","preferred_label":"...","language":"zh-CN","alternate_labels":["..."],
         "hidden_labels":[],"description":"...","broader_concept_iri":null,
         "mapped_entity_iri":null,"confidence":0.0,"reason":"...","source_chunk_ids":[1]}

        2. 为现有概念添加真正的同义词：
        {"action":"add_alias","target_concept_iri":"...","alternate_labels":["..."],
         "language":"zh-CN","confidence":0.0,"reason":"...","source_chunk_ids":[1]}

        3. 为现有概念添加上位关系或本体映射：
        {"action":"update","target_concept_iri":"...","broader_concept_iri":null,
         "mapped_entity_iri":null,"confidence":0.0,"reason":"...","source_chunk_ids":[1]}

        规则：
        - 区分同义词和子类型。“永磁电机”不是“电机”的别名；应创建更窄的概念，并设置其 broader concept。
        - 每个候选首选标签或替代标签必须逐字出现在至少一个被引用的来源分块中。如果来源只出现“泵”，不得合成“工业泵”等带上下文的新名称。
        - 替代标签必须是同一概念可互换的名称，不能是定义、描述、比喻、句子片段或相关短语。
        - 只有“每个目标概念必然都是该上位概念”成立时才能添加 broader concept。created-by、managed-by、used-by、contains 和 part-of 都不是上位关系。
        - 一个映射的本体实体只能对应一个受控概念。对于已映射实体的拼写或空格变体，应对现有概念提出 add_alias，而不是 create。
        - 只能复用下方明确提供的 IRI。不得伪造 target、broader 或 mapped IRI。
        - 不得重复现有首选、替代或隐藏标签。
        - 优先使用来源语言。解释保持简洁，并以证据为依据。
        - 不确定的噪声应跳过，不要勉强提出建议。proposals 为空是有效结果。
        - 下方的人工决定具有权威性；不得重复已被拒绝的建议。
        """;

    // -- conflict.resolution (English) ---------------------------------------
    // Source: backend/app/ontology/conflict_agent.py:32-46 (_SYSTEM).
    private const string ConflictResolutionEn = """
        You resolve ONE ontology TBox conflict by choosing the best available resolution.

        Respond with EXACTLY ONE JSON object per turn — one of:
        1) {"action":"get_neighborhood","name":"<a class label>"}
           → returns that class's superclasses/subclasses/related properties, to judge.
        2) {"action":"finish","resolution":"<a resolution id, or 'skip'>","confidence":<0..1>,"reason":"<short>"}

        Guidance:
        - Choose a resolution ONLY if you are confident it is correct. If genuinely unsure, finish with
          resolution "skip".
        - Duplicate classes: merge the two only if they are truly the SAME concept (not a subtype); pick the
          direction that KEEPS the more standard/general label as the target.
        - Over-specialized predicates (e.g. 拥有井/拥有计量站): judge whether the relation meaning is truly
          identical. These decisions require human confirmation even at high confidence.
        - Keep "reason" concise (<= 200 chars).
        """;

    // -- conflict.resolution (Simplified Chinese) ----------------------------
    // Source: backend/app/prompt_locales.py:333-344 (key "conflict.resolution")
    private const string ConflictResolutionZhCn = """
        你需要为一个本体 TBox 冲突选择最佳可用解决方案。

        每一轮必须且只能返回一个 JSON 对象，格式为以下两种之一：
        1) {"action":"get_neighborhood","name":"<类标签>"}
           → 返回该类的父类、子类和相关属性，用于判断。
        2) {"action":"finish","resolution":"<解决方案 id 或 skip>","confidence":<0..1>,"reason":"<简短理由>"}

        指导原则：
        - 只有确信解决方案正确时才选择它。如果确实不确定，以 resolution="skip" 结束。
        - 对重复类，只有两个类确实表示同一概念而不是子类型关系时才能合并；合并方向应保留更标准、更一般的标签作为目标。
        - 对过度专门化的谓词，例如"拥有井"和"拥有计量站"，需要判断关系含义是否真正相同。即使置信度很高，这类决定也需要人工确认。
        - reason 保持简洁，不超过 200 个字符。
        """;
}
