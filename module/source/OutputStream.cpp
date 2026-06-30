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

#include <OutputStream.hpp>
#include <cassert>
#include <cstring>

namespace MultiLibrary
{

OutputStream &OutputStream::operator<<( const bool &data )
{
	Write( &data, sizeof( bool ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const int8_t &data )
{
	Write( &data, sizeof( int8_t ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const uint8_t &data )
{
	Write( &data, sizeof( uint8_t ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const int16_t &data )
{
	Write( &data, sizeof( int16_t ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const uint16_t &data )
{
	Write( &data, sizeof( uint16_t ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const int32_t &data )
{
	Write( &data, sizeof( int32_t ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const uint32_t &data )
{
	Write( &data, sizeof( uint32_t ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const int64_t &data )
{
	Write( &data, sizeof( int64_t ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const uint64_t &data )
{
	Write( &data, sizeof( uint64_t ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const float &data )
{
	Write( &data, sizeof( float ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const double &data )
{
	Write( &data, sizeof( double ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const char &data )
{
	Write( &data, sizeof( char ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const char *data )
{
	assert( data != nullptr );

	Write( data, ( std::strlen( data ) + 1 ) * sizeof( char ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const std::string &data )
{
	Write( data.c_str( ), ( data.length( ) + 1 ) * sizeof( char ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const wchar_t &data )
{
	Write( &data, sizeof( wchar_t ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const wchar_t *data )
{
	assert( data != nullptr );

	Write( data, ( std::wcslen( data ) + 1 ) * sizeof( wchar_t ) );
	return *this;
}

OutputStream &OutputStream::operator<<( const std::wstring &data )
{
	Write( data.c_str( ), ( data.length( ) + 1 ) * sizeof( wchar_t ) );
	return *this;
}

} // namespace MultiLibrary