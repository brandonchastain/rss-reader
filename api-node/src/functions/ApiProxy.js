const { app } = require('@azure/functions');

// ============================================================================
// CONFIGURATION
// ============================================================================

// Allowed CORS origins - whitelist only trusted domains
const ALLOWED_ORIGINS = new Set([
    'https://rss.brandonchastain.com',
]);

// Headers that must NEVER be forwarded from client to backend
// Note: x-ms-client-principal is injected by SWA platform, not the browser,
// so we DO forward it. But we strip any attempt to send identity headers directly.
const FORBIDDEN_FORWARD_HEADERS = new Set([
    'x-user-id',
    'x-user-sub',
    'x-gateway-key',
    'host',
    'content-length',
    'connection',
    'keep-alive',
    'transfer-encoding'
]);

// ============================================================================
// MAIN HANDLER
// ============================================================================

app.http('ApiProxy', {
    methods: ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'OPTIONS'],
    authLevel: 'anonymous',
    route: '{*path}',
    handler: async (request, context) => {
        const path = request.params.path || '';
        
        // Handle CORS preflight
        if (request.method === 'OPTIONS') {
            return {
                status: 204,
                headers: getCorsHeaders(request)
            };
        }
        
        try {
            // Extract user identity from Easy Auth (platform-injected header)
            const userPrincipal = extractUserFromEasyAuth(context, request);
            
            if (!userPrincipal) {
                context.log('No Easy Auth principal found - user not authenticated');
                return {
                    status: 401,
                    headers: { 
                        'Content-Type': 'application/json',
                        ...getCorsHeaders(request)
                    },
                    body: JSON.stringify({ error: 'Authentication required' })
                };
            }

            // Forward the request to ACA backend
            const response = await forwardRequestToBackend(context, request, path, userPrincipal);
            return response;
            
        } catch (error) {
            context.log('Error processing request:', error);
            return {
                status: 500,
                headers: { 
                    'Content-Type': 'application/json',
                    ...getCorsHeaders(request)
                },
                body: JSON.stringify({ error: 'Internal server error' })
            };
        }
    }
});

// ============================================================================
// CORS - Whitelisted origins only
// ============================================================================

function getCorsHeaders(request) {
    const requestOrigin = request.headers.get('origin');
    
    // Only allow whitelisted origins, default to production domain
    const allowedOrigin = ALLOWED_ORIGINS.has(requestOrigin) 
        ? requestOrigin 
        : 'https://rss.brandonchastain.com';
    
    return {
        'Access-Control-Allow-Origin': allowedOrigin,
        'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, PATCH, OPTIONS',
        'Access-Control-Allow-Headers': 'Content-Type',  // NO identity headers - Easy Auth uses cookies
        'Access-Control-Allow-Credentials': 'true',      // Allow cookies for Easy Auth
        'Access-Control-Max-Age': '86400',
        'Vary': 'Origin'
    };
}

// ============================================================================
// EASY AUTH - Trust platform-injected headers
// ============================================================================

/**
 * Extract user from Easy Auth's platform-injected header.
 * 
 * SECURITY NOTE: This header is injected by the SWA platform AFTER validating
 * the user's session cookie. The browser cannot forge this header because:
 * 1. SWA sits between the browser and the function
 * 2. SWA strips any incoming x-ms-client-principal from the browser
 * 3. SWA only adds this header after validating the auth cookie
 * 
 * We do NOT allow this header in CORS, so browsers can't even attempt to send it.
 */
function extractUserFromEasyAuth(context, request) {
    const principalHeader = request.headers.get('x-ms-client-principal');
    
    if (!principalHeader) {
        context.log('x-ms-client-principal header not found (user not authenticated via Easy Auth)');
        return null;
    }

    try {
        const principalJson = Buffer.from(principalHeader, 'base64').toString('utf8');
        const principal = JSON.parse(principalJson);

        if (!principal || !principal.userId) {
            context.warn('Invalid client principal structure');
            return null;
        }

        context.log(`Easy Auth user: ${principal.userId} via ${principal.identityProvider}`);
        
        // Return the raw base64 header - backend will parse it the same way
        return principalHeader;
        
    } catch (error) {
        context.error('Error parsing Easy Auth principal:', error);
        return null;
    }
}

// ============================================================================
// REQUEST FORWARDING - Trusted headers only
// ============================================================================

async function forwardRequestToBackend(context, request, path, userPrincipal) {
    const backendUrl = process.env.RSSREADER_API_URL;
    const gatewayKey = process.env.RSSREADER_API_KEY;
    
    if (!backendUrl) throw new Error('RSSREADER_API_URL not configured');
    if (!gatewayKey) throw new Error('RSSREADER_API_KEY not configured');

    const cleanPath = path.startsWith('/') ? path.substring(1) : path;
    let targetUrl = `${backendUrl.replace(/\/$/, '')}/api/${cleanPath}`;
    
    const url = new URL(request.url);
    if (url.search) targetUrl += url.search;

    // Build trusted headers for backend
    const headers = {
        'X-Gateway-Key': gatewayKey,
        'X-User-Id': userPrincipal  // base64 ClientPrincipal from Easy Auth
    };

    // Forward safe headers from original request
    for (const [key, value] of request.headers.entries()) {
        const headerName = key.toLowerCase();
        
        // Skip forbidden headers
        if (FORBIDDEN_FORWARD_HEADERS.has(headerName)) {
            continue;
        }
        
        // Skip x-ms-* headers - we've already extracted what we need
        if (headerName.startsWith('x-ms-')) {
            continue;
        }
        
        // Skip authorization - backend uses gateway key, not user tokens
        if (headerName === 'authorization') {
            continue;
        }
        
        headers[key] = value;
    }

    const options = { method: request.method, headers };

    if (request.body && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method)) {
        options.body = await request.text();
    }

    context.log(`Forwarding ${request.method} to ${targetUrl}`);

    const response = await fetch(targetUrl, options);
    const responseText = await response.text();

    return {
        status: response.status,
        headers: { 
            'Content-Type': response.headers.get('content-type') || 'application/json',
            ...getCorsHeaders(request)
        },
        body: responseText
    };
}
