/*************************************************************************
 * MultiLibrary - danielga.bitbucket.org/multilibrary
 * A C++ library that covers multiple low level systems.
 *------------------------------------------------------------------------
 * Copyright (c) 2015, Daniel Almeida
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 *
 * 1. Redistributions of source code must retain the above copyright
 * notice, this list of conditions and the following disclaimer.
 *
 * 2. Redistributions in binary form must reproduce the above copyright
 * notice, this list of conditions and the following disclaimer in the
 * documentation and/or other materials provided with the distribution.
 *
 * 3. Neither the name of the copyright holder nor the names of its
 * contributors may be used to endorse or promote products derived from
 * this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
 * A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
 * HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
 * LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
 * DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
 * THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 *************************************************************************/

#pragma once

#include <cstdint>
#include <cstddef>

namespace MultiLibrary
{

/*!
 \brief Values that represent the type of seeking pretended.
 */
enum SeekMode
{
	SEEKMODE_SET, ///< Seeking is absolute
	SEEKMODE_CUR, ///< Seeking is relative to the current position
	SEEKMODE_END ///< Seeking is relative to the end of the file/buffer
};

/*!
 \brief An abstract class for objects that can act as data streams.
 */
class Stream
{
public:
	virtual bool IsValid( ) const = 0;

	explicit operator bool( ) const;

	bool operator!( ) const;

	/*!
	 \brief Set the current position for read/write operations.

	 \param position Position value.
	 \param mode (Optional) Type of seeking pretended.

	 \return true if it succeeds, false if it fails.
	 */
	virtual bool Seek( int64_t position, SeekMode mode = SEEKMODE_SET ) = 0;

	/*!
	 \brief Get the current position.

	 \return Current position.
	 */
	virtual int64_t Tell( ) const = 0;

	/*!
	 \brief Get the size.

	 \return Size of internal data.
	 */
	virtual int64_t Size( ) const = 0;

	/*!
	 \brief Return whether we reached end of file or not.

	 \return true if we reached end of file, false otherwise.
	 */
	virtual bool EndOfFile( ) const = 0;
};

} // namespace MultiLibrary