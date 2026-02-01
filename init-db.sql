-- Initialize Keycloak database and user
CREATE USER keycloak WITH PASSWORD 'keycloak';
CREATE DATABASE keycloak OWNER keycloak;
GRANT ALL PRIVILEGES ON DATABASE keycloak TO keycloak;

-- Set proper permissions for keycloak database
\c keycloak
GRANT ALL PRIVILEGES ON SCHEMA public TO keycloak;
