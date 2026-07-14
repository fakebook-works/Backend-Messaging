DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'messenger') THEN
        CREATE SCHEMA messenger;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS messenger."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'messenger') THEN
            CREATE SCHEMA messenger;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE TABLE messenger.conversations (
        id uuid NOT NULL,
        type character varying(16) NOT NULL,
        title character varying(200),
        avatar_url character varying(2048),
        direct_user_low_id bigint,
        direct_user_high_id bigint,
        current_sequence bigint NOT NULL DEFAULT 0,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_conversations PRIMARY KEY (id),
        CONSTRAINT ck_conversations_current_sequence CHECK (current_sequence >= 0),
        CONSTRAINT ck_conversations_direct_pair CHECK ((type = 'Direct' AND direct_user_low_id IS NOT NULL AND direct_user_high_id IS NOT NULL AND direct_user_low_id > 0 AND direct_user_low_id < direct_user_high_id) OR (type = 'Group' AND direct_user_low_id IS NULL AND direct_user_high_id IS NULL)),
        CONSTRAINT ck_conversations_type CHECK (type IN ('Direct', 'Group')),
        CONSTRAINT ck_conversations_updated_at CHECK (updated_at >= created_at)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE TABLE messenger.outbox_events (
        id uuid NOT NULL,
        topic character varying(200) NOT NULL,
        kind character varying(100) NOT NULL,
        payload_json jsonb NOT NULL,
        conversation_id uuid,
        message_id uuid,
        actor_user_id bigint,
        subject_user_id bigint,
        sequence bigint,
        occurred_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        processed_at timestamp with time zone,
        attempt_count integer NOT NULL DEFAULT 0,
        next_attempt_at timestamp with time zone,
        last_error character varying(4000),
        CONSTRAINT pk_outbox_events PRIMARY KEY (id),
        CONSTRAINT ck_outbox_events_actor_user_id CHECK (actor_user_id IS NULL OR actor_user_id > 0),
        CONSTRAINT ck_outbox_events_attempt_count CHECK (attempt_count >= 0),
        CONSTRAINT ck_outbox_events_sequence CHECK (sequence IS NULL OR sequence >= 0),
        CONSTRAINT ck_outbox_events_subject_user_id CHECK (subject_user_id IS NULL OR subject_user_id > 0)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE TABLE messenger.users (
        user_id bigint NOT NULL,
        status character varying(16) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        deleted_at timestamp with time zone,
        CONSTRAINT pk_users PRIMARY KEY (user_id),
        CONSTRAINT ck_users_deleted_state CHECK ((status = 'Active' AND deleted_at IS NULL) OR (status = 'Deleted' AND deleted_at IS NOT NULL)),
        CONSTRAINT ck_users_status CHECK (status IN ('Active', 'Deleted')),
        CONSTRAINT ck_users_user_id_positive CHECK (user_id > 0)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE TABLE messenger.conversation_participants (
        conversation_id uuid NOT NULL,
        user_id bigint NOT NULL,
        role character varying(16) NOT NULL,
        joined_at timestamp with time zone NOT NULL,
        left_at timestamp with time zone,
        last_delivered_sequence bigint NOT NULL DEFAULT 0,
        last_read_sequence bigint NOT NULL DEFAULT 0,
        CONSTRAINT pk_conversation_participants PRIMARY KEY (conversation_id, user_id),
        CONSTRAINT ck_conversation_participants_membership_dates CHECK (left_at IS NULL OR left_at >= joined_at),
        CONSTRAINT ck_conversation_participants_receipts CHECK (last_delivered_sequence >= 0 AND last_read_sequence >= 0 AND last_read_sequence <= last_delivered_sequence),
        CONSTRAINT ck_conversation_participants_role CHECK (role IN ('Admin', 'Member')),
        CONSTRAINT ck_conversation_participants_user_id CHECK (user_id > 0),
        CONSTRAINT fk_conversation_participants_conversations FOREIGN KEY (conversation_id) REFERENCES messenger.conversations (id) ON DELETE CASCADE,
        CONSTRAINT fk_conversation_participants_users FOREIGN KEY (user_id) REFERENCES messenger.users (user_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE TABLE messenger.messages (
        id uuid NOT NULL,
        conversation_id uuid NOT NULL,
        sender_user_id bigint NOT NULL,
        sequence bigint NOT NULL,
        client_message_id uuid NOT NULL,
        text character varying(10000),
        reply_to_message_id uuid,
        created_at timestamp with time zone NOT NULL,
        edited_at timestamp with time zone,
        deleted_at timestamp with time zone,
        CONSTRAINT pk_messages PRIMARY KEY (id),
        CONSTRAINT ck_messages_client_message_id CHECK (client_message_id <> '00000000-0000-0000-0000-000000000000'::uuid),
        CONSTRAINT ck_messages_delete_date CHECK (deleted_at IS NULL OR deleted_at >= created_at),
        CONSTRAINT ck_messages_edit_date CHECK (edited_at IS NULL OR edited_at >= created_at),
        CONSTRAINT ck_messages_sender_user_id CHECK (sender_user_id > 0),
        CONSTRAINT ck_messages_sequence CHECK (sequence > 0),
        CONSTRAINT fk_messages_conversations FOREIGN KEY (conversation_id) REFERENCES messenger.conversations (id) ON DELETE CASCADE,
        CONSTRAINT fk_messages_reply_to_message FOREIGN KEY (reply_to_message_id) REFERENCES messenger.messages (id) ON DELETE RESTRICT,
        CONSTRAINT fk_messages_users FOREIGN KEY (sender_user_id) REFERENCES messenger.users (user_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE TABLE messenger.presence (
        user_id bigint NOT NULL,
        is_online boolean NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_presence PRIMARY KEY (user_id),
        CONSTRAINT ck_presence_user_id CHECK (user_id > 0),
        CONSTRAINT fk_presence_users FOREIGN KEY (user_id) REFERENCES messenger.users (user_id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE TABLE messenger.message_attachments (
        message_id uuid NOT NULL,
        ordinal integer NOT NULL,
        url character varying(2048) NOT NULL,
        CONSTRAINT pk_message_attachments PRIMARY KEY (message_id, ordinal),
        CONSTRAINT ck_message_attachments_https_url CHECK (url LIKE 'https://%'),
        CONSTRAINT ck_message_attachments_ordinal CHECK (ordinal >= 0 AND ordinal < 10),
        CONSTRAINT fk_message_attachments_messages FOREIGN KEY (message_id) REFERENCES messenger.messages (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE TABLE messenger.message_reactions (
        message_id uuid NOT NULL,
        user_id bigint NOT NULL,
        emoji character varying(32) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_message_reactions PRIMARY KEY (message_id, user_id),
        CONSTRAINT ck_message_reactions_emoji CHECK (length(btrim(emoji)) > 0),
        CONSTRAINT ck_message_reactions_updated_at CHECK (updated_at >= created_at),
        CONSTRAINT ck_message_reactions_user_id CHECK (user_id > 0),
        CONSTRAINT fk_message_reactions_messages FOREIGN KEY (message_id) REFERENCES messenger.messages (id) ON DELETE CASCADE,
        CONSTRAINT fk_message_reactions_users FOREIGN KEY (user_id) REFERENCES messenger.users (user_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE INDEX ix_conversation_participants_active_conversation ON messenger.conversation_participants (conversation_id) WHERE left_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE INDEX ix_conversation_participants_active_user ON messenger.conversation_participants (user_id, conversation_id) WHERE left_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE INDEX ix_conversations_updated_at ON messenger.conversations (updated_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE UNIQUE INDEX ux_conversations_direct_pair ON messenger.conversations (direct_user_low_id, direct_user_high_id) WHERE type = 'Direct';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE INDEX ix_message_reactions_user_id ON messenger.message_reactions (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE INDEX ix_messages_reply_to_message_id ON messenger.messages (reply_to_message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE UNIQUE INDEX ux_messages_conversation_sequence ON messenger.messages (conversation_id, sequence DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE UNIQUE INDEX ux_messages_sender_client_message ON messenger.messages (sender_user_id, client_message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE INDEX ix_outbox_events_pending ON messenger.outbox_events (next_attempt_at, created_at) WHERE processed_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE INDEX ix_outbox_events_pending_topic ON messenger.outbox_events (topic, created_at) WHERE processed_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE INDEX ix_presence_online_expiry ON messenger.presence (expires_at) WHERE is_online = TRUE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    CREATE INDEX ix_users_status ON messenger.users (status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713194105_InitialMessagingSchema') THEN
    INSERT INTO messenger."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260713194105_InitialMessagingSchema', '8.0.15');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713201435_AddOutboxRetentionIndex') THEN
    CREATE INDEX ix_outbox_events_processed ON messenger.outbox_events (processed_at) WHERE processed_at IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM messenger."__EFMigrationsHistory" WHERE "MigrationId" = '20260713201435_AddOutboxRetentionIndex') THEN
    INSERT INTO messenger."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260713201435_AddOutboxRetentionIndex', '8.0.15');
    END IF;
END $EF$;
COMMIT;

