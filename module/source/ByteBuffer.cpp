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

#include <ByteBuffer.hpp>
#include <stdexcept>
#include <cassert>
#include <cstring>

namespace MultiLibrary
{

ByteBuffer::ByteBuffer( ) :
	end_of_file( true ),
	buffer_offset( 0 )
{ }

ByteBuffer::ByteBuffer( size_t size ) :
	end_of_file( true ),
	buffer_offset( 0 )
{
	Resize( size );
}

ByteBuffer::ByteBuffer( const uint8_t *copy_buffer, size_t size ) :
	end_of_file( true ),
	buffer_offset( 0 )
{
	Assign( copy_buffer, size );
}

ByteBuffer::~ByteBuffer( )
{ }

bool ByteBuffer::IsValid( ) const
{
	return !EndOfFile( );
}

ByteBuffer::operator bool( ) const
{
	return IsValid( );
}

bool ByteBuffer::operator!( ) const
{
	return !IsValid( );
}

int64_t ByteBuffer::Tell( ) const
{
	return static_cast<int64_t>( buffer_offset );
}

int64_t ByteBuffer::Size( ) const
{
	return static_cast<int64_t>( buffer_internal.size( ) );
}

size_t ByteBuffer::Capacity( ) const
{
	return buffer_internal.capacity( );
}

bool ByteBuffer::Seek( int64_t position, SeekMode mode )
{
	assert( mode != SEEKMODE_SET || ( mode == SEEKMODE_SET && position >= 0 ) );
	assert( mode != SEEKMODE_CUR || ( mode == SEEKMODE_CUR && Tell( ) + position >= 0 ) );
	assert( mode != SEEKMODE_END || ( mode == SEEKMODE_END && Size( ) + position >= 0 ) );

	int64_t temp;
	switch( mode )
	{
	case SEEKMODE_SET:
		buffer_offset = static_cast<size_t>( position > 0 ? position : 0 );
		break;

	case SEEKMODE_CUR:
		temp = Tell( ) + position;
		buffer_offset = static_cast<size_t>( temp > 0 ? temp : 0 );
		break;

	case SEEKMODE_END:
		temp = Size( ) + position;
		buffer_offset = static_cast<size_t>( temp > 0 ? temp : 0 );
		break;

	default:
		return false;
	}

	end_of_file = false;
	return true;
}

bool ByteBuffer::EndOfFile( ) const
{
	return end_of_file;
}

uint8_t *ByteBuffer::GetBuffer( )
{
	return buffer_internal.data();
}

const uint8_t *ByteBuffer::GetBuffer( ) const
{
	return &buffer_internal[0];
}

void ByteBuffer::Clear( )
{
	buffer_internal.clear( );
	buffer_offset = 0;
	end_of_file = false;
}

void ByteBuffer::Reserve( size_t capacity )
{
	buffer_internal.reserve( capacity );
}

void ByteBuffer::Resize( size_t size )
{
	buffer_internal.resize( size );
}

void ByteBuffer::ShrinkToFit( )
{
	std::vector<uint8_t>( buffer_internal ).swap( buffer_internal );
}

void ByteBuffer::Assign( const uint8_t *copy_buffer, size_t size )
{
	assert( copy_buffer != nullptr && size != 0 );

	buffer_internal.assign( copy_buffer, copy_buffer + size );
	buffer_offset = 0;
	end_of_file = false;
}

size_t ByteBuffer::Read( void *value, size_t size )
{
	assert( value != nullptr && size != 0 );

	if( buffer_offset >= buffer_internal.size( ) )
	{
		end_of_file = true;
		return 0;
	}

	size_t clamped = buffer_internal.size( ) - buffer_offset;
	if( clamped > size )
		clamped = size;
	std::memcpy( value, &buffer_internal[buffer_offset], clamped );
	buffer_offset += clamped;
	if( clamped < size )
		end_of_file = true;

	return clamped;
}

size_t ByteBuffer::Write( const void *value, size_t size )
{
	assert( value != nullptr && size != 0 );

	if( buffer_internal.size( ) < buffer_offset + size )
		Resize( buffer_offset + size );

	std::memcpy( &buffer_internal[buffer_offset], value, size );
	buffer_offset += size;
	return size;
}

} // namespace MultiLibrary