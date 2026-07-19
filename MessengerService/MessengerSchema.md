# Messenger database schema

Schema PostgreSQL hiện tại của Messenger Service. Media của tin nhắn được lưu dưới dạng một attachment snapshot; Upload Server vẫn là nơi quản lý file vật lý và vòng đời pending/committed.

```sql
create table "__EFMigrationsHistory"
(
"MigrationId"    varchar(150) not null
constraint "PK___EFMigrationsHistory"
primary key,
"ProductVersion" varchar(32)  not null
);

alter table "__EFMigrationsHistory"
owner to fakebook;

create table conversations
(
id                  uuid                     not null
constraint pk_conversations
primary key,
type                varchar(16)              not null
constraint ck_conversations_type
check ((type)::text = ANY ((ARRAY ['Direct'::character varying, 'Group'::character varying])::text[])),
title               varchar(200),
avatar_url          varchar(2048),
direct_user_low_id  bigint,
direct_user_high_id bigint,
current_sequence    bigint default 0         not null
constraint ck_conversations_current_sequence
check (current_sequence >= 0),
created_at          timestamp with time zone not null,
updated_at          timestamp with time zone not null,
constraint ck_conversations_direct_pair
check ((((type)::text = 'Direct'::text) AND (direct_user_low_id IS NOT NULL) AND
(direct_user_high_id IS NOT NULL) AND (direct_user_low_id > 0) AND
(direct_user_low_id < direct_user_high_id)) OR
(((type)::text = 'Group'::text) AND (direct_user_low_id IS NULL) AND (direct_user_high_id IS NULL))),
constraint ck_conversations_updated_at
check (updated_at >= created_at)
);

alter table conversations
owner to fakebook;

create index ix_conversations_updated_at
on conversations (updated_at desc);

create unique index ux_conversations_direct_pair
on conversations (direct_user_low_id, direct_user_high_id)
where ((type)::text = 'Direct'::text);

create table outbox_events
(
id              uuid                     not null
constraint pk_outbox_events
primary key,
topic           varchar(200)             not null,
kind            varchar(100)             not null,
payload_json    jsonb                    not null,
conversation_id uuid,
message_id      uuid,
actor_user_id   bigint
constraint ck_outbox_events_actor_user_id
check ((actor_user_id IS NULL) OR (actor_user_id > 0)),
subject_user_id bigint
constraint ck_outbox_events_subject_user_id
check ((subject_user_id IS NULL) OR (subject_user_id > 0)),
sequence        bigint
constraint ck_outbox_events_sequence
check ((sequence IS NULL) OR (sequence >= 0)),
occurred_at     timestamp with time zone not null,
created_at      timestamp with time zone not null,
processed_at    timestamp with time zone,
attempt_count   integer default 0        not null
constraint ck_outbox_events_attempt_count
check (attempt_count >= 0),
next_attempt_at timestamp with time zone,
last_error      varchar(4000)
);

alter table outbox_events
owner to fakebook;

create index ix_outbox_events_pending
on outbox_events (next_attempt_at, created_at)
where (processed_at IS NULL);

create index ix_outbox_events_pending_topic
on outbox_events (topic, created_at)
where (processed_at IS NULL);

create index ix_outbox_events_processed
on outbox_events (processed_at)
where (processed_at IS NOT NULL);

create table users
(
user_id    bigint                   not null
constraint pk_users
primary key
constraint ck_users_user_id_positive
check (user_id > 0),
status     varchar(16)              not null
constraint ck_users_status
check ((status)::text = ANY ((ARRAY ['Active'::character varying, 'Deleted'::character varying])::text[])),
created_at timestamp with time zone not null,
deleted_at timestamp with time zone,
constraint ck_users_deleted_state
check ((((status)::text = 'Active'::text) AND (deleted_at IS NULL)) OR
(((status)::text = 'Deleted'::text) AND (deleted_at IS NOT NULL)))
);

alter table users
owner to fakebook;

create index ix_users_status
on users (status);

create table conversation_participants
(
conversation_id         uuid                     not null
constraint fk_conversation_participants_conversations
references conversations
on delete cascade,
user_id                 bigint                   not null
constraint fk_conversation_participants_users
references users
on delete restrict
constraint ck_conversation_participants_user_id
check (user_id > 0),
role                    varchar(16)              not null
constraint ck_conversation_participants_role
check ((role)::text = ANY ((ARRAY ['Admin'::character varying, 'Member'::character varying])::text[])),
joined_at               timestamp with time zone not null,
left_at                 timestamp with time zone,
last_delivered_sequence bigint default 0         not null,
last_read_sequence      bigint default 0         not null,
constraint pk_conversation_participants
primary key (conversation_id, user_id),
constraint ck_conversation_participants_membership_dates
check ((left_at IS NULL) OR (left_at >= joined_at)),
constraint ck_conversation_participants_receipts
check ((last_delivered_sequence >= 0) AND (last_read_sequence >= 0) AND
(last_read_sequence <= last_delivered_sequence))
);

alter table conversation_participants
owner to fakebook;

create index ix_conversation_participants_active_conversation
on conversation_participants (conversation_id)
where (left_at IS NULL);

create index ix_conversation_participants_active_user
on conversation_participants (user_id, conversation_id)
where (left_at IS NULL);

create table messages
(
id                  uuid                     not null
constraint pk_messages
primary key,
conversation_id     uuid                     not null
constraint fk_messages_conversations
references conversations
on delete cascade,
sender_user_id      bigint                   not null
constraint fk_messages_users
references users
on delete restrict
constraint ck_messages_sender_user_id
check (sender_user_id > 0),
sequence            bigint                   not null
constraint ck_messages_sequence
check (sequence > 0),
client_message_id   uuid                     not null
constraint ck_messages_client_message_id
check (client_message_id <> '00000000-0000-0000-0000-000000000000'::uuid),
text                varchar(10000),
reply_to_message_id uuid
constraint fk_messages_reply_to_message
references messages
on delete restrict,
created_at          timestamp with time zone not null,
edited_at           timestamp with time zone,
deleted_at          timestamp with time zone,
constraint ck_messages_delete_date
check ((deleted_at IS NULL) OR (deleted_at >= created_at)),
constraint ck_messages_edit_date
check ((edited_at IS NULL) OR (edited_at >= created_at))
);

alter table messages
owner to fakebook;

create index ix_messages_reply_to_message_id
on messages (reply_to_message_id);

create unique index ux_messages_conversation_sequence
on messages (conversation_id asc, sequence desc);

create unique index ux_messages_sender_client_message
on messages (sender_user_id, client_message_id);

create table presence
(
user_id    bigint                   not null
constraint pk_presence
primary key
constraint fk_presence_users
references users
on delete cascade
constraint ck_presence_user_id
check (user_id > 0),
is_online  boolean                  not null,
expires_at timestamp with time zone not null,
updated_at timestamp with time zone not null
);

alter table presence
owner to fakebook;

create index ix_presence_online_expiry
on presence (expires_at)
where (is_online = true);

create table message_attachments
(
message_id    uuid          not null
constraint fk_message_attachments_messages
references messages
on delete cascade,
ordinal       integer       not null
constraint ck_message_attachments_ordinal
check ((ordinal >= 0) AND (ordinal < 10)),
url           varchar(2048) not null
constraint ck_message_attachments_https_url
check (((url)::text ~~ '/media/files/%'::text) OR ((url)::text ~~ 'https://%'::text)),
asset_id      varchar(128),
media_type    varchar(16),
content_type  varchar(128),
original_name varchar(255),
size_bytes    bigint,
width         integer,
height        integer,
duration_ms   bigint,
thumbnail_url varchar(2048),
constraint ck_message_attachments_media_type
check ((media_type IS NULL) OR ((media_type)::text = ANY ((ARRAY ['image'::character varying, 'video'::character varying, 'audio'::character varying, 'file'::character varying])::text[]))),
constraint ck_message_attachments_metadata_nonnegative
check (((size_bytes IS NULL) OR (size_bytes >= 0)) AND ((width IS NULL) OR (width >= 0)) AND ((height IS NULL) OR (height >= 0)) AND ((duration_ms IS NULL) OR (duration_ms >= 0))),
constraint pk_message_attachments
primary key (message_id, ordinal)
);

alter table message_attachments
owner to fakebook;

create table message_reactions
(
message_id uuid                     not null
constraint fk_message_reactions_messages
references messages
on delete cascade,
user_id    bigint                   not null
constraint fk_message_reactions_users
references users
on delete restrict
constraint ck_message_reactions_user_id
check (user_id > 0),
emoji      varchar(32)              not null
constraint ck_message_reactions_emoji
check (length(btrim((emoji)::text)) > 0),
created_at timestamp with time zone not null,
updated_at timestamp with time zone not null,
constraint pk_message_reactions
primary key (message_id, user_id),
constraint ck_message_reactions_updated_at
check (updated_at >= created_at)
);

alter table message_reactions
owner to fakebook;

create index ix_message_reactions_user_id
on message_reactions (user_id);
```

## Quy ước attachment

- Một message có tối đa 10 attachment; `ordinal` giữ nguyên thứ tự người gửi đã chọn.
- `url` chấp nhận managed path `/media/files/...` hoặc HTTPS URL đã được Messenger Service kiểm tra host. HTTP URL bên ngoài không được chấp nhận.
- `asset_id` liên kết attachment với asset do Upload Server tạo, phục vụ finalize/delete và kiểm tra ownership.
- `media_type` là snapshot phân loại `image`, `video`, `audio` hoặc `file`; đây không phải foreign key hay bảng con theo từng loại media.
- `content_type`, `original_name` và `size_bytes` giữ metadata Upload Server trả về để frontend không phải suy luận lại từ URL sau khi reload.
- `width`, `height`, `duration_ms` và `thumbnail_url` là metadata tùy chọn cho collage, video và audio. Dữ liệu cũ có thể để `NULL` và được suy luận tạm từ URL/content type.
- File vật lý và trạng thái pending/committed không được nhân đôi trong schema này; Upload Server là nguồn dữ liệu chính cho vòng đời asset.
