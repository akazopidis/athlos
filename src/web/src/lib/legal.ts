// The version of the Terms the customer accepts at checkout (design §5.4). It is sent with every
// checkout, and ordering-api refuses a version other than its Legal:TermsVersion, so changing the
// terms means changing both, and nobody pays under terms they were not shown.
export const TermsVersion = '2026-10-26'

export const TermsDate = '26 October 2026'

// The Terms for coaches, accepted when a workspace is opened; tenancy-api refuses a version other than
// its Legal:CoachTermsVersion, the same way.
export const CoachTermsVersion = '2026-10-26'

export const CoachTermsDate = '26 October 2026'
