const button = document.getElementById('capture');
const status = document.getElementById('status');

button.addEventListener('click', async () => {
    button.disabled = true;
    status.className = '';
    status.textContent = 'Reading the current job page…';

    try {
        const [tab] = await chrome.tabs.query({
            active: true,
            currentWindow: true
        });

        if (!tab?.id || !/^https?:/.test(tab.url || '')) {
            throw new Error(
                'Open a public job-posting page before using the extension.'
            );
        }

        const [{ result: captured }] =
            await chrome.scripting.executeScript({
                target: {
                    tabId: tab.id
                },
                func: captureRenderedJob
            });

        if (!captured) {
            throw new Error(
                'The current job page could not be read.'
            );
        }

        status.textContent =
            'Sending the job to your local tracker…';

        const response = await sendCapture(captured);

        if (!response.ok) {
            const errorMessage = await response.text();

            throw new Error(
                errorMessage
                || 'The local tracker rejected the capture.'
            );
        }

        const result = await response.json();

        status.className = 'success';
        status.textContent =
            'Captured. Opening the prefilled application…';

        await chrome.tabs.create({
            url:
                'https://localhost:7248/applications/new'
                + `?captureToken=${encodeURIComponent(result.token)}`
        });
    } catch (error) {
        status.className = 'error';
        status.textContent =
            error.message
            || 'The job could not be captured.';
    } finally {
        button.disabled = false;
    }
});

async function sendCapture(captured) {
    try {
        return await fetch(
            'https://localhost:7248/api/job-captures',
            {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(captured)
            }
        );
    } catch {
        return await fetch(
            'http://localhost:5248/api/job-captures',
            {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(captured)
            }
        );
    }
}

async function captureRenderedJob() {
    const currentHost = location.hostname
        .replace(/^www\./, '')
        .toLowerCase();

    /*
     * Ashby uses separate Overview and Application tabs.
     * Switch to Overview before reading the rendered page.
     */
    if (currentHost.includes('ashbyhq.com')) {
        const clickableElements = [
            ...document.querySelectorAll(
                'button, [role="tab"], a'
            )
        ];

        const overviewTab = clickableElements.find(
            element =>
                (
                    element.innerText
                    || element.textContent
                    || ''
                )
                    .trim()
                    .toLowerCase() === 'overview'
        );

        if (overviewTab) {
            overviewTab.click();

            await new Promise(resolve =>
                setTimeout(resolve, 1000)
            );
        }
    }

    const clean = value =>
        (value || '')
            .replace(/\u00a0/g, ' ')
            .replace(/[ \t]+/g, ' ')
            .replace(/\n{3,}/g, '\n\n')
            .trim();

    const firstText = selectors => {
        for (const selector of selectors) {
            const element =
                document.querySelector(selector);

            if (!element) {
                continue;
            }

            const value = clean(
                element.innerText
                || element.textContent
            );

            if (value) {
                return value;
            }
        }

        return '';
    };

    const getMeta = (...selectors) => {
        for (const selector of selectors) {
            const value = clean(
                document.querySelector(selector)?.content
            );

            if (value) {
                return value;
            }
        }

        return '';
    };

    const text = clean(document.body?.innerText);

    const host = location.hostname
        .replace(/^www\./, '')
        .toLowerCase();

    /*
     * Locate standard JobPosting JSON-LD.
     */
    let structured = null;

    for (
        const script of document.querySelectorAll(
            'script[type="application/ld+json"]'
        )
    ) {
        try {
            const value = JSON.parse(
                script.textContent
            );

            const findJobPosting = node => {
                if (!node || typeof node !== 'object') {
                    return null;
                }

                const type = node['@type'];

                if (
                    String(type).toLowerCase()
                    === 'jobposting'
                    || (
                        Array.isArray(type)
                        && type.some(
                            item =>
                                String(item).toLowerCase()
                                === 'jobposting'
                        )
                    )
                ) {
                    return node;
                }

                for (
                    const child of Object.values(node)
                ) {
                    const found =
                        findJobPosting(child);

                    if (found) {
                        return found;
                    }
                }

                return null;
            };

            structured = findJobPosting(value);

            if (structured) {
                break;
            }
        } catch {
            /*
             * Ignore invalid JSON-LD and continue
             * checking other structured-data blocks.
             */
        }
    }

    /*
     * Determine the position title.
     */
    const heading = firstText([
        '[data-automation-id="jobPostingHeader"] h2',
        '[data-automation-id="jobPostingHeader"] h1',
        '[data-testid*="job-title"]',
        '[data-testid*="jobTitle"]',
        '[class*="job-title"]',
        '[class*="jobTitle"]',
        'main h1',
        'article h1',
        'h1',
        'main h2'
    ]);

    const openGraphTitle = getMeta(
        'meta[property="og:title"]',
        'meta[name="twitter:title"]'
    );

    const documentTitle = clean(
        document.title
    ).split(/\s+[|–—]\s+/)[0];

    const positionTitle = clean(
        structured?.title
        || heading
        || openGraphTitle
        || documentTitle
    );

    /*
     * Determine the company name.
     */
    const knownCompany = (() => {
        if (
            host === 'jobs.citi.com'
            || host.endsWith('.jobs.citi.com')
        ) {
            return 'Citi';
        }

        if (
            host === 'careers.microsoft.com'
            || host.endsWith(
                '.careers.microsoft.com'
            )
            || host
            === 'apply.careers.microsoft.com'
        ) {
            return 'Microsoft';
        }

        return '';
    })();

    const organization =
        structured?.hiringOrganization;

    const structuredCompany = clean(
        typeof organization === 'object'
            ? organization?.name
            : ''
    );

    const domCompany = firstText([
        '[data-automation-id="company"]',
        '[data-testid*="company-name"]',
        '[data-testid*="companyName"]',
        '[data-company-name]',
        '[class*="company-name"]',
        '[class*="companyName"]'
    ]);

    let metaCompany = getMeta(
        'meta[property="og:site_name"]',
        'meta[name="application-name"]'
    );

    metaCompany = metaCompany
        .replace(
            /^careers?\s+(?:at|with)\s+/i,
            ''
        )
        .replace(
            /^jobs?\s+(?:at|with)\s+/i,
            ''
        )
        .replace(/\s+careers?$/i, '')
        .trim();

    const aboutCompany = clean(
        text.match(
            /(?:^|\n)About\s+([^\n]+)/
        )?.[1]
    );

    const companyName = clean(
        knownCompany
        || structuredCompany
        || domCompany
        || metaCompany
        || aboutCompany
    );

    /*
     * Determine the location.
     */
    let locationText = '';

    const jobLocation =
        Array.isArray(structured?.jobLocation)
            ? structured.jobLocation[0]
            : structured?.jobLocation;

    const address = jobLocation?.address;

    if (typeof address === 'string') {
        locationText = address;
    } else if (address) {
        locationText = [
            address.addressLocality,
            address.addressRegion,
            address.addressCountry
        ]
            .filter(Boolean)
            .join(', ');
    }

    if (!locationText) {
        locationText = firstText([
            '[data-automation-id="locations"]',
            '[data-automation-id="location"]',
            '[data-testid*="location"]',
            '[class*="job-location"]',
            '[class*="jobLocation"]',
            '[class~="location"]'
        ]);
    }

    /*
     * Determine the employer's job ID.
     */
    const visibleJobId = clean(
        text.match(
            /(?:Job Req Id|Job Req ID|Job number|Job ID|Requisition ID|Req ID)\s*:?\s*\n?\s*([A-Za-z0-9_-]+)/
        )?.[1]
    );

    const query =
        new URLSearchParams(location.search);

    const pathSegments = location.pathname
        .split('/')
        .filter(Boolean);

    const ashbyJobId =
        host.includes('ashbyhq.com')
            ? pathSegments.find(
                segment =>
                    /^[0-9a-f]{8}-[0-9a-f-]{27,}$/i
                        .test(segment)
            )
            : null;

    const jobId =
        visibleJobId
        || query.get('joblistid')
        || query.get('jobId')
        || query.get('jobid')
        || query.get('job_id')
        || query.get('requisitionId')
        || query.get('requisition_id')
        || query.get('reqId')
        || query.get('req_id')
        || ashbyJobId
        || query.get('jr_id');

    /*
     * Determine the ATS/job-board source.
     */
    let source = host;

    if (host.includes('hirebridge')) {
        source =
            'Hirebridge via Jobright';
    } else if (
        host.includes('myworkdayjobs')
    ) {
        source =
            'Workday via Jobright';
    } else if (
        host.includes('greenhouse')
    ) {
        source =
            'Greenhouse via Jobright';
    } else if (host.includes('lever.co')) {
        source =
            'Lever via Jobright';
    } else if (
        host.includes('ashbyhq.com')
    ) {
        source =
            'Ashby via Jobright';
    }

    return {
        url: location.href,
        pageTitle: document.title,
        positionTitle,
        companyName,
        location: clean(locationText),
        jobId: clean(jobId),
        source,
        renderedText: text,
        capturedAtUtc:
            new Date().toISOString()
    };
}