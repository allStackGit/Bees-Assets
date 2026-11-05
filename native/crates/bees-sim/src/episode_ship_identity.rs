//! World-local port of Scripts/Scenes/RlEpisodeShipIdentity.cs.
//!
//! Each participating ship receives a stable, episode-scoped float identity
//! from a permutation of the 23-bit scalar code space. Runtime IDs and spawn
//! order are not leaked as ordered numbers into a neural observation.
//!
//! Unity seeds its separate identity PRNG from a Guid; deterministic native
//! evaluation instead supplies a world/episode-owned RandomStream. The
//! permutation and encoding match Unity, not its nondeterministic seed.

use std::collections::HashMap;
use crate::simulation::RandomStream;

pub const IDENTITY_BITS: u32 = 23;
pub const IDENTITY_CAPACITY: u32 = 1 << IDENTITY_BITS;
const MASK: u32 = IDENTITY_CAPACITY - 1;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ShipIdentityError {
    InvalidMultiplier,
    InvalidOffset,
    Exhausted,
}

/// Stable mapping from runtime ship identity to one permuted scalar code.
/// Mutable state belongs to an episode, never a process-global registry.
#[derive(Clone, Debug)]
pub struct EpisodeShipIdentities {
    ordinals: HashMap<i64, u32>,
    next_ordinal: u32,
    multiplier: u32,
    offset: u32,
}

impl EpisodeShipIdentities {
    pub fn new(multiplier: u32, offset: u32) -> Result<Self, ShipIdentityError> {
        if multiplier == 0 || multiplier >= IDENTITY_CAPACITY ||
            multiplier & 1 == 0 {
            return Err(ShipIdentityError::InvalidMultiplier);
        }
        if offset >= IDENTITY_CAPACITY {
            return Err(ShipIdentityError::InvalidOffset);
        }
        Ok(Self {
            ordinals: HashMap::new(),
            next_ordinal: 0,
            multiplier,
            offset,
        })
    }

    /// Unity draws multiplier in [1, 2^23) and forces it odd; offset spans
    /// [0, 2^23). The native RNG is explicitly passed by the episode owner,
    /// rather than using a static System.Random/Guid source.
    pub fn from_random(random: &mut RandomStream) -> Self {
        let multiplier =
            (random.next_below(u64::from(IDENTITY_CAPACITY - 1)) as u32 + 1) | 1;
        let offset = random.next_below(u64::from(IDENTITY_CAPACITY)) as u32;
        Self::new(multiplier, offset)
            .expect("valid generated identity permutation")
    }

    /// Explicitly start a new episode with a new permutation, discarding
    /// previous ship IDs and ordinal counters.
    pub fn reset(&mut self, random: &mut RandomStream) {
        let next = Self::from_random(random);
        self.multiplier = next.multiplier;
        self.offset = next.offset;
        self.ordinals.clear();
        self.next_ordinal = 0;
    }

    pub fn assigned_count(&self) -> usize { self.ordinals.len() }

    pub fn get_or_assign(&mut self, ship_id: i64) -> Result<f32, ShipIdentityError> {
        let ordinal = if let Some(&ordinal) = self.ordinals.get(&ship_id) {
            ordinal
        } else {
            if self.next_ordinal >= IDENTITY_CAPACITY {
                return Err(ShipIdentityError::Exhausted);
            }
            let ordinal = self.next_ordinal;
            self.next_ordinal += 1;
            self.ordinals.insert(ship_id, ordinal);
            ordinal
        };
        Ok(encode_ordinal(ordinal, self.multiplier, self.offset))
    }
}

/// RlEpisodeShipIdentity.EncodeOrdinal, with bounds already checked by the
/// episode owner. Each possible ordinal maps bijectively to a distinct code.
#[inline]
fn encode_ordinal(ordinal: u32, multiplier: u32, offset: u32) -> f32 {
    let permuted = ordinal.wrapping_mul(multiplier)
        .wrapping_add(offset) & MASK;
    -1.0 + 2.0 * permuted as f32 / MASK as f32
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn same_ship_retains_permuted_scalar_and_episode_reset_reassigns() {
        let mut identities = EpisodeShipIdentities::new(5, 100).unwrap();
        let first = identities.get_or_assign(1001).unwrap();
        let second = identities.get_or_assign(1002).unwrap();
        assert_eq!(identities.get_or_assign(1001).unwrap(), first);
        assert_ne!(first, second);
        assert_eq!(identities.assigned_count(), 2);
        assert_eq!(first, -1.0 + 2.0 * 100.0 / MASK as f32);
        let mut random = RandomStream::new(50);
        identities.reset(&mut random);
        assert_eq!(identities.assigned_count(), 0);
        let current = identities.get_or_assign(1001).unwrap();
        assert_ne!(first, current);
    }

    #[test]
    fn even_multiplier_and_out_of_range_offset_are_rejected() {
        assert!(matches!(
            EpisodeShipIdentities::new(2, 0),
            Err(ShipIdentityError::InvalidMultiplier)
        ));
        assert!(matches!(
            EpisodeShipIdentities::new(1, IDENTITY_CAPACITY),
            Err(ShipIdentityError::InvalidOffset)
        ));
    }
}
