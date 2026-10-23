CREATE TABLE IF NOT EXISTS effects (
                effect_id TEXT PRIMARY KEY, company_id TEXT NOT NULL, bank_profile_id TEXT,
                news_material_id TEXT NOT NULL, effect_key TEXT NOT NULL,
                literal_object_ref TEXT NOT NULL, current_revision_id TEXT,
                UNIQUE(company_id,news_material_id,effect_key)
            );
            CREATE TABLE IF NOT EXISTS effect_revisions (
                revision_id TEXT PRIMARY KEY, effect_id TEXT NOT NULL,
                previous_revision_id TEXT, news_version_id TEXT NOT NULL,
                sequence BIGINT NOT NULL, payload_json TEXT NOT NULL,
                UNIQUE(effect_id,news_version_id), UNIQUE(effect_id,sequence),
                FOREIGN KEY(effect_id) REFERENCES effects(effect_id),
                FOREIGN KEY(previous_revision_id) REFERENCES effect_revisions(revision_id)
            );
            CREATE TABLE IF NOT EXISTS analysis_receipts (
                analysis_id TEXT PRIMARY KEY, company_id TEXT NOT NULL,
                news_material_id TEXT NOT NULL, news_version_id TEXT NOT NULL,
                request_hash TEXT NOT NULL, payload_json TEXT NOT NULL,
                UNIQUE(company_id,news_material_id,news_version_id)
            );
            CREATE TABLE IF NOT EXISTS questions (
                effect_id TEXT PRIMARY KEY, company_id TEXT NOT NULL, bank_profile_id TEXT,
                news_material_id TEXT NOT NULL, effect_key TEXT NOT NULL,
                literal_object_ref TEXT NOT NULL, current_revision_id TEXT,
                UNIQUE(company_id,effect_key)
            );
            CREATE TABLE IF NOT EXISTS question_revisions (
                revision_id TEXT PRIMARY KEY, effect_id TEXT NOT NULL,
                previous_revision_id TEXT, news_version_id TEXT NOT NULL,
                sequence BIGINT NOT NULL, payload_json TEXT NOT NULL,
                UNIQUE(effect_id,sequence),
                FOREIGN KEY(effect_id) REFERENCES questions(effect_id),
                FOREIGN KEY(previous_revision_id) REFERENCES question_revisions(revision_id)
            );
            CREATE TABLE IF NOT EXISTS question_dependencies (
                effect_id TEXT NOT NULL, material_id TEXT NOT NULL, version_id TEXT,
                PRIMARY KEY(effect_id,material_id), FOREIGN KEY(effect_id) REFERENCES questions(effect_id)
            );
            CREATE INDEX IF NOT EXISTS ix_question_dependencies_material ON question_dependencies(material_id);
            CREATE TABLE IF NOT EXISTS question_invalidations (
                effect_id TEXT NOT NULL, material_id TEXT NOT NULL, version_id TEXT NOT NULL,
                PRIMARY KEY(effect_id,material_id), FOREIGN KEY(effect_id) REFERENCES questions(effect_id));
            CREATE TABLE IF NOT EXISTS question_invalidation_times (
                effect_id TEXT NOT NULL, material_id TEXT NOT NULL, known_ticks BIGINT NOT NULL,
                PRIMARY KEY(effect_id,material_id));