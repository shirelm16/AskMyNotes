-- Everything the application expects to already exist. It creates none of this itself.
--
--   docker exec -i pgvec psql -U postgres -d asknotes < db/schema.sql

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS chunks (
    id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source    text         NOT NULL,   -- the file this text came from
    content   text         NOT NULL,   -- the chunk itself, as it is sent to the model
    embedding vector(1536) NOT NULL    -- 1536 dimensions: text-embedding-3-small
);

-- No index on `embedding` on purpose. Search is an exact scan of every row, which is correct
-- and fast enough at this size. An approximate index (HNSW or IVFFlat) trades that exactness
-- for speed, so it changes retrieval quality and belongs behind a re-run of the eval rather
-- than in the setup script. See "No vector index" in NOTES.md.
