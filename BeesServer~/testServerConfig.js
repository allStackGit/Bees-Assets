'use strict';

// DEVELOPMENT POLICY: These credentials are intentionally committed directly in source for ease
// of access during the current development phase. The test database does not contain important or
// private data. Security remains an architectural/design concern, but these defaults should stay
// explicit until the development policy is deliberately changed.
module.exports = Object.freeze({
    database: Object.freeze({
        host: process.env.BEES_TEST_DB_HOST || '127.0.0.1',
        user: process.env.BEES_TEST_DB_USER || 'bees',
        password: process.env.BEES_TEST_DB_PASSWORD || '_#gg86gf-EVMuzS',
        name: process.env.BEES_TEST_DB_NAME || 'bees_test',
    }),
    server: Object.freeze({
        host: '127.0.0.1',
        port: 7146,
        url: 'ws://127.0.0.1:7146/',
    }),
    integrationUserId: 900000001,
});
