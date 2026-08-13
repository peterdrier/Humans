using Humans.Domain.Attributes;

// The analyzer seam, the discovery marker, and what SectionControllerFeatureProvider keys
// off so the internal TourController is discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md).
[assembly: Section("Tour")]
