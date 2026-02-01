const { app } = require('@azure/functions');

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
            // Extract user identity from Static Web Apps
            const userId = extractUserId(context, request);
            
            if (!userId) {
                context.log('No user identity found in request');
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
            const response = await forwardRequestToBackend(context, request, path, userId);
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

function getCorsHeaders(request) {
    const origin = request.headers.get('origin'); // || '*'; 
    return {
        'Access-Control-Allow-Origin': origin,
        'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, PATCH, OPTIONS',
        'Access-Control-Allow-Headers': 'Content-Type, Authorization, x-ms-client-principal',
        'Access-Control-Max-Age': '86400'
    };
}

function extractUserId(context, request) {
    const principalHeader = request.headers.get('x-ms-client-principal');
    
    if (!principalHeader) {
        context.log('x-ms-client-principal header not found');
        return null;
    }

    try {
        const principalJson = Buffer.from(principalHeader, 'base64').toString('utf8');
        const principal = JSON.parse(principalJson);

        if (!principal) {
            context.warn('Failed to deserialize client principal');
            return null;
        }

        context.log(`Extracted user from provider: ${principal.identityProvider}`);
        
        // Return the full principal header (base64 encoded) for the backend to parse
        return principalHeader;
        
    } catch (error) {
        context.error('Error extracting user ID:', error);
        return null;
    }
}

async function forwardRequestToBackend(context, request, path, userId) {
    const backendUrl = process.env.RSSREADER_API_URL;
    const gatewayKey = process.env.RSSREADER_API_KEY;
    
    if (!backendUrl) throw new Error('RSSREADER_API_URL not configured');
    if (!gatewayKey) throw new Error('RSSREADER_API_KEY not configured');

    const cleanPath = path.startsWith('/') ? path.substring(1) : path;
    // Add /api/ prefix back since Azure Functions strips it from the route
    let targetUrl = `${backendUrl.replace(/\/$/, '')}/api/${cleanPath}`;
    
    const url = new URL(request.url);
    if (url.search) targetUrl += url.search;

    const headers = {
        'X-Gateway-Key': gatewayKey,
        'X-User-Id': userId
    };

    for (const [key, value] of request.headers.entries()) {
        const headerName = key.toLowerCase();
        if (headerName !== 'host' && headerName !== 'content-length') {
            headers[key] = value;
        }
    }

    const options = { method: request.method, headers };

    if (request.body && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method)) {
        options.body = await request.text();
    }

    context.log(`Forwarding ${request.method} to ${targetUrl} for user ${userId}`);

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
