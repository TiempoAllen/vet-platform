# SEO Specification

## 1. Purpose

This document defines the SEO requirements for the veterinary management platform.

SEO work must improve discoverability of public-facing pages without exposing or indexing private clinic, client, patient, clinical, billing, inventory, or administrative data.

SEO is a product requirement for public pages and a security/privacy requirement for authenticated pages.

---

## 2. SEO Principles

1. Public pages should be crawlable when intended for search engines.
2. Private application pages must not be intentionally indexed.
3. Never expose client, patient, medical, financial, or clinic-private data through SEO metadata.
4. Use unique, descriptive page titles and meta descriptions.
5. Use semantic HTML and a logical heading hierarchy.
6. Use clean, stable, human-readable URLs.
7. Avoid duplicate or near-duplicate indexable pages.
8. Optimize for mobile and performance.
9. Use structured data only when it accurately represents visible page content.
10. SEO must never override authentication, authorization, or privacy controls.

---

## 3. Public vs Private Pages

### Indexable public pages

Examples:

- `/`
- `/features`
- `/pricing`
- `/about`
- `/contact`
- `/solutions`
- `/resources`
- `/blog`
- `/blog/:slug`
- `/veterinary-management-software`
- `/veterinary-practice-management`
- `/online-vet-software`

These pages may be indexed when they contain useful, original content.

### Private application pages

Examples:

- `/login`
- `/register`
- `/dashboard`
- `/patients`
- `/patients/:id`
- `/clients`
- `/clients/:id`
- `/appointments`
- `/consultations`
- `/prescriptions`
- `/vaccinations`
- `/billing`
- `/inventory`
- `/reports`
- `/settings`
- `/admin`

These pages must not be intended for search engine indexing.

Do not rely on `robots.txt` alone to protect private content. Authentication and authorization are the primary protection mechanisms.

---

## 4. Page Metadata

Every indexable public page should have:

- Unique `<title>`
- Unique meta description
- Canonical URL
- Appropriate robots directive
- Open Graph metadata
- Twitter/social metadata where supported
- Correct language metadata
- Descriptive heading structure

### Title rules

Titles should:

- Clearly describe the page
- Put the primary topic near the beginning when natural
- Avoid keyword stuffing
- Be unique across indexable pages
- Include the product/brand name when useful

Example:

`Veterinary Practice Management Software | [Brand]`

Feature page:

`Veterinary Practice Management Features | [Brand]`

Blog:

`How to Improve Veterinary Appointment Scheduling | [Brand]`

---

## 5. Meta Descriptions

Each indexable page should have a concise, useful meta description describing what the user will find.

Avoid:

- Keyword stuffing
- Repeated descriptions across many pages
- Misleading claims
- Sensitive information
- Automatically generated descriptions containing private data

Example:

`Manage appointments, patient records, billing, inventory, and clinical workflows with veterinary practice management software.`

---

## 6. Canonical URLs

Every indexable page should have a canonical URL.

Canonical URLs should:

- Use HTTPS
- Use the preferred production domain
- Avoid unnecessary query parameters
- Be stable
- Use one consistent trailing-slash convention
- Avoid duplicate URL variants

Example:

`https://example.com/features`

Do not create multiple canonical URLs for the same page.

---

## 7. URL Structure

Use short, readable URLs.

Good:

- `/features`
- `/pricing`
- `/blog/veterinary-inventory-management`
- `/solutions/small-veterinary-clinics`

Avoid:

- `/page?id=123`
- `/content/abc123`
- `/blog/post-847392`
- Excessively nested URLs

URLs should describe the content rather than expose internal database identifiers.

---

## 8. Heading Structure

Each important page should have one clear primary heading.

Recommended structure:

```text
H1: Main page topic
  H2: Major section
    H3: Supporting subsection
```

Do not use headings only for visual styling.

The heading hierarchy should make sense when read without CSS.

---

## 9. Semantic HTML

Use semantic elements where appropriate:

- `<header>`
- `<nav>`
- `<main>`
- `<section>`
- `<article>`
- `<footer>`
- `<button>`
- `<form>`
- `<label>`

Avoid building the entire public website from generic `<div>` elements when semantic elements are appropriate.

Links should be actual `<a>` elements and should contain descriptive text.

Avoid generic link text such as:

- `Click here`
- `Learn more`
- `Read more`

when a more descriptive label is practical.

---

## 10. Internal Linking

Public pages should have a logical internal-link structure.

Examples:

- Homepage → Features
- Features → Individual feature pages
- Features → Pricing
- Blog articles → Relevant feature pages
- Blog articles → Related articles
- Blog → Veterinary software/product pages

Internal links should use descriptive anchor text.

Avoid excessive or artificial internal linking.

---

## 11. XML Sitemap

Generate an XML sitemap for public indexable pages.

The sitemap should:

- Include only canonical, indexable URLs
- Exclude authenticated/private application pages
- Exclude redirects
- Exclude 404 pages
- Exclude duplicate URLs
- Update automatically when relevant content changes

If the site grows substantially, use sitemap indexes where appropriate.

---

## 12. Robots.txt

Provide a valid `/robots.txt`.

Its purpose is to communicate crawl preferences, not to provide access control.

Private data must remain protected by authentication and authorization.

Example concept:

```text
User-agent: *
Allow: /

Sitemap: https://example.com/sitemap.xml
```

Do not blindly disallow application routes if public resources needed for rendering or legitimate crawling could be affected.

---

## 13. Noindex Requirements

Authenticated/private pages should normally return an appropriate noindex directive.

Example:

```html
<meta name="robots" content="noindex, nofollow">
```

For sensitive application routes, consider sending the directive as an HTTP response header as well.

Important:

`noindex` is not a security boundary.

Sensitive information must never be publicly accessible merely because a page has `noindex`.

---

## 14. Structured Data

Use Schema.org structured data where appropriate and truthful.

Potential public-page types include:

- Organization
- SoftwareApplication
- WebSite
- WebPage
- Article
- BreadcrumbList
- FAQPage where the page genuinely contains qualifying FAQ content

Structured data must describe the visible page content.

Do not create structured data containing:

- Private clinic information
- Client information
- Patient information
- Medical records
- Internal financial data
- Fake reviews
- Fake ratings
- Misleading claims

Validate structured data before production.

---

## 15. Open Graph / Social Sharing

Public pages should support social previews.

Recommended fields:

- `og:title`
- `og:description`
- `og:url`
- `og:type`
- `og:image`
- `og:site_name`

Blog/article pages may additionally use article-specific metadata where appropriate.

Images should:

- Have appropriate dimensions
- Be optimized
- Represent the page accurately
- Avoid sensitive veterinary/client/patient information

---

## 16. Images

All meaningful images should have useful alternative text.

Good:

```html
<img
  src="/images/veterinary-dashboard.webp"
  alt="Veterinary clinic management dashboard showing today's appointments"
/>
```

Decorative images should use appropriate empty alternative text when applicable.

Do not place important textual content only inside images.

Use modern image formats where practical and provide responsive image sizes.

---

## 17. Performance and SEO

SEO implementation must not create unnecessary performance problems.

The frontend should prioritize:

- Fast initial rendering
- Small JavaScript bundles
- Code splitting
- Lazy loading for non-critical resources
- Optimized images
- Proper caching
- Efficient API requests
- Avoiding unnecessary client-side rendering for content that should be crawlable

Public content that is important for search discovery should be rendered in a way that search engines can reliably access.

---

## 18. JavaScript Rendering

Do not assume search engines will execute every application behavior correctly.

For important public content:

- Prefer server-rendered or statically generated HTML where appropriate
- Ensure meaningful content exists in the rendered document
- Avoid requiring user interaction before essential content appears
- Ensure navigation links are crawlable
- Provide stable URLs for indexable content

Private application functionality may remain highly interactive.

---

## 19. Mobile SEO

The public website must be responsive.

Requirements:

- Mobile-friendly layouts
- Readable text
- Usable navigation
- Touch-friendly controls
- No unnecessary horizontal scrolling
- Responsive images
- Fast mobile loading
- Accessible forms

Test important pages at common mobile viewport sizes.

---

## 20. Accessibility and SEO

Accessibility and SEO should be implemented together where possible.

Requirements include:

- Semantic HTML
- Proper labels
- Keyboard navigation
- Descriptive link text
- Useful image alt text
- Logical headings
- Sufficient focus visibility
- Accessible forms
- Meaningful page titles

Do not sacrifice accessibility solely for SEO.

---

## 21. Local SEO

If the product supports individual veterinary clinics with public profiles or clinic websites, local SEO may be implemented separately.

Potential public information:

- Clinic name
- Public address
- Public phone number
- Opening hours
- Services
- Website
- Public location information

Only information explicitly configured as public by the clinic should be exposed.

Never expose:

- Internal notes
- Client records
- Patient records
- Private staff information
- Internal schedules
- Financial information
- Clinical records

If public clinic pages are supported, consider appropriate structured data such as `VeterinaryCare`.

---

## 22. Blog / Content Strategy

If a blog or resource center exists, content should target genuine user needs.

Potential topics:

- Veterinary practice management
- Appointment scheduling
- Veterinary inventory management
- Clinic workflow
- Patient record management
- Veterinary billing
- Practice reporting
- Clinic operations
- Veterinary technology

Content should be:

- Original
- Useful
- Accurate
- Well structured
- Clearly authored where appropriate
- Updated when information becomes outdated

Avoid producing large volumes of low-value pages solely to target keywords.

---

## 23. Duplicate Content

Avoid duplicate indexable pages caused by:

- URL parameters
- Sorting
- Filtering
- Pagination variants
- Trailing slash variants
- Case differences
- Duplicate routes
- Multiple URLs pointing to the same content

Use canonical URLs, redirects, or appropriate indexing directives as needed.

---

## 24. Redirects

When public URLs change:

- Use appropriate permanent redirects when the move is permanent
- Update internal links
- Update canonical URLs
- Update sitemap entries
- Avoid redirect chains
- Avoid redirecting unrelated pages simply to preserve SEO value

Do not redirect users from public pages into authenticated pages without a clear product reason.

---

## 25. Error Pages

Provide useful:

- 404 page
- 500/server error handling

The 404 page should:

- Return the correct HTTP status
- Explain that the page was not found
- Provide useful navigation
- Avoid being indexed as normal content

---

## 26. Authentication and SEO Boundary

SEO must never weaken application security.

The following data must never become publicly indexable:

- Client names and contact information
- Patient names and medical information
- Diagnoses
- Treatments
- Prescriptions
- Vaccination records
- Clinical notes
- Invoices
- Payments
- Inventory records
- Internal reports
- Staff-private information

Search engine crawlers must never receive unauthorized application data.

---

## 27. Multi-Tenant SEO

If the platform serves multiple veterinary clinics, tenant boundaries must remain strict.

Public clinic pages, if implemented, must only expose data belonging to that clinic and explicitly marked as public.

A clinic must never be able to access or expose another clinic's data through:

- Public URLs
- Metadata
- Structured data
- Sitemap generation
- Search endpoints
- Cached responses
- Server-rendered pages

Canonical URLs must also identify the correct tenant/public site.

---

## 28. Authentication Pages

Login, registration, password reset, and similar account pages generally should not be indexed.

Recommended behavior:

- Correct authentication controls
- `noindex`
- No sensitive information in page metadata
- No client/patient data in page source
- No private information in analytics URLs or query parameters

---

## 29. SEO-Friendly Routing

Frontend routing should support:

- Stable URLs
- Direct navigation
- Browser refresh
- Proper HTTP status handling where server rendering is used
- Canonical URLs
- Dynamic metadata for public pages
- 404 handling

Do not use internal numeric database IDs in public URLs unless there is a strong product reason.

Prefer stable slugs for public content.

---

## 30. Metadata Architecture

SEO metadata should be managed centrally rather than duplicated inconsistently across components.

Create reusable metadata functionality supporting:

```text
title
description
canonical
robots
ogTitle
ogDescription
ogImage
ogUrl
twitterCard
```

Public pages should provide page-specific metadata.

Private application pages should use the appropriate noindex policy.

---

## 31. Technical SEO Checklist

Before production:

- [ ] HTTPS enabled
- [ ] One canonical production domain
- [ ] Unique titles
- [ ] Unique meta descriptions where appropriate
- [ ] Canonical URLs configured
- [ ] XML sitemap generated
- [ ] robots.txt configured
- [ ] Private routes excluded from indexing
- [ ] 404 page implemented
- [ ] Correct HTTP status codes
- [ ] Redirects tested
- [ ] Semantic HTML reviewed
- [ ] Heading hierarchy reviewed
- [ ] Image alt text reviewed
- [ ] Open Graph metadata implemented
- [ ] Structured data validated where used
- [ ] Mobile responsiveness tested
- [ ] Performance tested
- [ ] No sensitive data appears in public HTML
- [ ] No sensitive data appears in metadata
- [ ] No sensitive data appears in structured data
- [ ] Multi-tenant isolation tested

---

## 32. Search Engine Verification

Before launch, verify the public website using appropriate search engine webmaster tools.

Check:

- Sitemap submission
- Crawlability
- Indexing status
- Canonical selection
- Mobile usability
- Structured data
- Page experience
- Unexpected indexed URLs

Never submit private application URLs for indexing.

---

## 33. SEO Testing

Automated tests should verify important SEO behavior.

Examples:

```text
Public pages:
- Have a title
- Have appropriate metadata
- Have canonical URLs
- Are intended to be indexable
- Have valid heading structure

Private pages:
- Return authentication requirements
- Do not expose private data
- Have noindex behavior where appropriate

Sitemap:
- Contains only intended public URLs

Robots:
- Is syntactically valid
- References the sitemap

Public content:
- Does not leak tenant/private data
```

---

## 34. Definition of Done

SEO implementation is complete when:

1. Public pages have intentional SEO metadata.
2. Private application pages are protected from indexing.
3. No private clinical, client, financial, inventory, or tenant data is exposed.
4. Sitemap and robots.txt are implemented appropriately.
5. Canonical URLs are consistent.
6. Public routing is stable and crawlable.
7. Structured data is valid where used.
8. Social sharing metadata works.
9. Mobile and performance requirements are addressed.
10. Automated SEO/security checks pass.
11. Production deployment has been manually reviewed for accidental indexing or data exposure.

---

## 35. Agent Instructions

### Backend agent

The backend must:

- Provide correct HTTP status codes.
- Support public/private route separation.
- Prevent unauthorized data access.
- Prevent tenant data leakage.
- Generate or serve sitemap/robots resources where backend-owned.
- Avoid exposing sensitive information through APIs used by public pages.
- Ensure caching does not cross tenant boundaries.
- Support correct redirects and canonical URL configuration where applicable.

### Frontend agent

The frontend must:

- Implement reusable metadata handling.
- Implement page titles and descriptions.
- Implement canonical URLs.
- Implement robots directives.
- Implement Open Graph metadata.
- Use semantic HTML.
- Maintain accessible heading hierarchy.
- Create SEO-friendly public routes.
- Keep authenticated pages private/noindex.
- Optimize public-page performance.
- Never render private application data into public/SEO content.

### Shared rule

When a feature changes a public URL, metadata, sitemap entry, rendering strategy, or indexing behavior, update this document and the relevant architecture/API/UI documentation.
